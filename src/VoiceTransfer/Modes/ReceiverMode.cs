using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Receiver mode: captures audio (from microphone or WAV file), runs the
/// Goertzel-based FSK demodulator, extracts the data frame, and writes
/// the decoded file to disk.
/// 
/// The Goertzel algorithm acts as a very narrow bandpass filter at our
/// FSK frequencies (1800/2200 Hz). Human voice energy is spread across
/// hundreds of frequencies, so it appears as low-level noise in each bin.
/// Our FSK tone concentrates all its energy in a single bin, making it
/// stand out clearly above the voice "floor" -- even during conversation.
/// </summary>
public static class ReceiverMode
{
    public static void Run(IAudioBackend audio, string outputFile, string? inputWav, int deviceIndex, int timeoutSeconds, TransmissionProfile profile, string? password = null)
    {
        float[] samples;

        if (inputWav != null)
        {
            samples = ReadWav(inputWav);
            if (samples.Length == 0)
            {
                return;
            }
        }
        else
        {
            samples = RecordFromMicrophone(audio, deviceIndex, timeoutSeconds);
            if (samples.Length == 0)
            {
                Log.Error("No audio captured");
                return;
            }
        }

        profile.LogSettings();

        Log.Information("Processing {Samples} samples ({Duration:F1}s)...",
            samples.Length, (double)samples.Length / Constants.SampleRate);

        // Preprocess: DC removal -> bandpass filter -> normalization
        // Critical for over-the-air reception where room noise, mic coloring,
        // and variable gain would otherwise corrupt Goertzel power estimates.
        Log.Information("Preprocessing: DC removal, bandpass filter, normalization...");
        samples = DspFilters.Preprocess(samples, profile);

        // Demodulate
        var decoded = DemodulateAndDecode(samples, profile, password);

        if (decoded != null)
        {
            File.WriteAllBytes(outputFile, decoded);
            Log.Information("Successfully decoded {Bytes} bytes -> {File}", decoded.Length, outputFile);
        }
        else
        {
            Log.Error("Failed to decode data from audio. Possible causes:");
            Log.Error("  - No FSK signal detected");
            Log.Error("  - Signal too weak (try moving closer to speaker)");
            Log.Error("  - CRC mismatch (data corrupted by noise)");
            Log.Error("  - Wrong alignment (try recording a cleaner signal)");
        }
    }

    /// <summary>
    /// Core demodulation pipeline: samples -> bits -> frame -> file data.
    /// Tries multiple alignment offsets to find the best one.
    /// </summary>
    public static byte[]? DemodulateAndDecode(float[] samples, TransmissionProfile profile, string? password = null)
    {
        Log.Debug("Step 1: Signal detection...");
        var demod = new FskDemodulator(profile);

        // Find approximate start of signal (first block with energy at FSK frequencies)
        var signalStart = FindSignalStart(samples, profile);
        if (signalStart >= 0)
        {
            Log.Debug("Signal detected at sample {Start} ({Time:F3}s)",
                signalStart, (double)signalStart / Constants.SampleRate);

            // Find best bit alignment using preamble correlation
            Log.Debug("Step 2: Bit synchronization via preamble...");
            var searchLen = Math.Min(profile.PreambleBits * profile.SamplesPerBit * 2, samples.Length - signalStart);
            var alignedStart = demod.FindBestAlignment(samples, signalStart, searchLen);

            Log.Debug("Aligned to sample {Start}", alignedStart);

            // Try the best alignment and a few nearby offsets
            for (var nudge = -profile.SamplesPerBit / 2; nudge <= profile.SamplesPerBit / 2; nudge += profile.SamplesPerBit / 8)
            {
                var tryStart = alignedStart + nudge;
                if (tryStart < 0)
                {
                    continue;
                }

                var result = TryDemodulate(samples, tryStart, demod, profile.FecRepeat, password);
                if (result != null)
                {
                    Log.Debug("Successful decode at offset {Offset} (nudge={Nudge})", tryStart, nudge);
                    return result;
                }
            }

            // Brute force: try every single-sample offset in a bit-width window
            Log.Debug("Step 3: Brute-force alignment search...");
            for (var offset = Math.Max(0, signalStart - profile.SamplesPerBit);
                 offset < signalStart + profile.SamplesPerBit * 2 && offset < samples.Length;
                 offset++)
            {
                var result = TryDemodulate(samples, offset, demod, profile.FecRepeat, password);
                if (result != null)
                {
                    Log.Debug("Brute-force decode succeeded at offset {Offset}", offset);
                    return result;
                }
            }
        }
        else
        {
            Log.Debug("Preamble detection failed -- falling back to full scan");
        }

        // Step 4: Full-scan fallback -- preamble detection may fail over the air
        // even though the data signal is present. Scan the entire audio at coarse
        // intervals, trying to decode at each position.
        Log.Debug("Step 4: Full-scan fallback (scanning entire audio)...");
        var stepSize = profile.SamplesPerBit / 2; // half-bit resolution
        for (var offset = 0; offset < samples.Length - profile.SamplesPerBit * 16; offset += stepSize)
        {
            var result = TryDemodulate(samples, offset, demod, profile.FecRepeat, password);
            if (result != null)
            {
                Log.Debug("Full-scan decode succeeded at offset {Offset} ({Time:F3}s)",
                    offset, (double)offset / Constants.SampleRate);
                return result;
            }
        }

        return null;
    }

    private static byte[]? TryDemodulate(float[] samples, int startOffset, FskDemodulator demod, int fecRepeat, string? password)
    {
        var (bits, valid) = demod.DemodulateAll(samples, startOffset);

        // Count valid bits to check if there's enough signal
        var validCount = valid.Count(v => v);
        if (validCount < 16)
        {
            return null; // too few valid bits for a frame
        }

        // Strategy 1: Find longest run allowing generous gaps of invalid bits.
        // Room reverb and noise bursts can create long stretches of ambiguous blocks,
        // but the underlying bit guess is often still correct. FEC handles errors.
        var maxGap = Math.Max(20, fecRepeat * 8);
        var runStart = -1;
        var bestRunStart = 0;
        var bestRunEnd = 0;
        var currentGap = 0;

        for (var i = 0; i < valid.Length; i++)
        {
            if (valid[i])
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                currentGap = 0;

                var runEnd = i;
                if (runEnd - runStart > bestRunEnd - bestRunStart)
                {
                    bestRunStart = runStart;
                    bestRunEnd = runEnd;
                }
            }
            else
            {
                if (runStart >= 0)
                {
                    currentGap++;
                    if (currentGap > maxGap)
                    {
                        runStart = -1;
                        currentGap = 0;
                    }
                }
            }
        }

        var bestRunLen = bestRunEnd - bestRunStart + 1;
        if (bestRunLen >= 16)
        {
            var runBits = new bool[bestRunLen];
            Array.Copy(bits, bestRunStart, runBits, 0, bestRunLen);

            var result = FrameCodec.Decode(runBits, fecRepeat, password);
            if (result != null)
            {
                return result;
            }
        }

        // Strategy 2: Try ALL demodulated bits regardless of validity.
        // Over-the-air, many blocks register as "invalid" (low confidence) but
        // the best-guess bit value is still often correct. FEC + CRC will catch
        // truly corrupted data, so it's safe to attempt.
        if (bits.Length >= 16)
        {
            return FrameCodec.Decode(bits, fecRepeat, password);
        }

        return null;
    }

    /// <summary>
    /// Find the sample index where FSK signal first appears.
    /// Uses a sliding window that counts blocks with clear FSK energy.
    /// Tolerates individual invalid blocks (caused by reverb, noise bursts).
    /// </summary>
    private static int FindSignalStart(float[] samples, TransmissionProfile profile)
    {
        var blockSize = profile.SamplesPerBit;
        var numBlocks = samples.Length / blockSize;
        const int windowSize = 12; // wider window for statistical robustness
        const int minValid = 4;    // need at least this many valid blocks in the window
        const int minAlternations = 3; // relaxed alternation requirement

        if (numBlocks < windowSize)
        {
            return numBlocks > 0 ? 0 : -1; // audio too short, try from beginning
        }

        for (var i = 0; i <= numBlocks - windowSize; i++)
        {
            var validCount = 0;
            var alternations = 0;
            bool? prevValidBit = null;

            for (var j = 0; j < windowSize; j++)
            {
                var offset = (i + j) * blockSize;
                var markPower = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqMark);
                var spacePower = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqSpace);
                var maxPower = Math.Max(markPower, spacePower);
                var minPower = Math.Min(markPower, spacePower);

                if (maxPower < profile.SignalThreshold)
                {
                    continue; // skip, don't break
                }

                if (minPower > 0 && maxPower / minPower < profile.DecisionRatio)
                {
                    continue; // skip ambiguous block
                }

                validCount++;
                var bit = markPower > spacePower;
                if (prevValidBit.HasValue && bit != prevValidBit.Value)
                {
                    alternations++;
                }

                prevValidBit = bit;
            }

            if (validCount >= minValid && alternations >= minAlternations)
            {
                return i * blockSize;
            }
        }

        return -1;
    }

    private static float[] ReadWav(string path)
    {
        if (!File.Exists(path))
        {
            Log.Error("WAV file not found: {File}", path);
            return Array.Empty<float>();
        }

        var samples = WavFile.Read(path, out var sampleRate, out var channels);
        Log.Information("Reading WAV: {Rate}Hz, {Ch}ch, {Duration:F1}s",
            sampleRate, channels, (double)samples.Length / sampleRate);

        return samples;
    }

    private static float[] RecordFromMicrophone(IAudioBackend audio, int deviceIndex, int timeoutSeconds)
    {
        var deviceNames = audio.GetInputDeviceNames();
        Log.Information("Available input devices:");
        for (var i = 0; i < deviceNames.Count; i++)
            Log.Information("  [{Index}] {Name}", i, deviceNames[i]);

        if (deviceNames.Count == 0)
        {
            Log.Error("No audio input devices found");
            return Array.Empty<float>();
        }

        if (deviceIndex >= deviceNames.Count)
        {
            Log.Error("Device index {Index} out of range (0-{Max})", deviceIndex, deviceNames.Count - 1);
            return Array.Empty<float>();
        }

        using var recorder = audio.CreateRecorder(deviceIndex);
        var allSamples = new List<float>();
        var readBuffer = new float[4096];

        Log.Information("Recording for {Seconds}s... (speak normally, FSK signal will be filtered)", timeoutSeconds);

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var count = recorder.Read(readBuffer);
            if (count > 0)
            {
                for (var i = 0; i < count; i++)
                    allSamples.Add(readBuffer[i]);
            }
            else
            {
                Thread.Sleep(5);
            }
        }

        var samples = allSamples.ToArray();
        Log.Information("Recorded {Samples} samples ({Duration:F1}s)", samples.Length,
            (double)samples.Length / Constants.SampleRate);

        return samples;
    }
}
