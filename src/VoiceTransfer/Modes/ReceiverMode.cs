using Serilog;
using VoiceTransfer.Audio;
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

        // Find approximate start of signal (first block with energy at FSK frequencies)
        var signalStart = FindSignalStart(samples, profile);
        if (signalStart < 0)
        {
            Log.Debug("No FSK signal detected in audio");
            return null;
        }

        Log.Debug("Signal detected at sample {Start} ({Time:F3}s)",
            signalStart, (double)signalStart / Constants.SampleRate);

        // Find best bit alignment using preamble correlation
        Log.Debug("Step 2: Bit synchronization via preamble...");
        var searchLen = Math.Min(profile.PreambleBits * profile.SamplesPerBit * 2, samples.Length - signalStart);
        var demod = new FskDemodulator(profile);
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

        return null;
    }

    private static byte[]? TryDemodulate(float[] samples, int startOffset, FskDemodulator demod, int fecRepeat, string? password)
    {
        var (bits, valid) = demod.DemodulateAll(samples, startOffset);

        // Count valid bits to check if there's enough signal
        var validCount = valid.Count(v => v);
        if (validCount < 20)
        {
            return null; // too few valid bits for a frame
        }

        // Find longest run allowing small gaps of invalid bits.
        // FEC handles any bit errors from low-confidence blocks, but comfort noise
        // (many consecutive invalid blocks) correctly splits runs.
        var maxGap = Math.Max(3, fecRepeat * 2);
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
        if (bestRunLen < 20)
        {
            return null;
        }

        // Extract the run (including any gap bits -- they have best-guess values)
        var runBits = new bool[bestRunLen];
        Array.Copy(bits, bestRunStart, runBits, 0, bestRunLen);

        // Try to decode frame (with FEC)
        return FrameCodec.Decode(runBits, fecRepeat, password);
    }

    /// <summary>
    /// Find the sample index where FSK signal first appears by detecting the
    /// preamble's alternating bit pattern. This reliably distinguishes actual
    /// FSK signal from comfort noise (where power ratios can pass by chance).
    /// </summary>
    private static int FindSignalStart(float[] samples, TransmissionProfile profile)
    {
        var blockSize = profile.SamplesPerBit;
        var numBlocks = samples.Length / blockSize;
        const int windowSize = 8; // check 8 consecutive blocks for alternating pattern

        for (var i = 0; i <= numBlocks - windowSize; i++)
        {
            var valid = true;
            var alternations = 0;
            bool? prevBit = null;

            for (var j = 0; j < windowSize; j++)
            {
                var offset = (i + j) * blockSize;
                var markPower = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqMark);
                var spacePower = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqSpace);
                var maxPower = Math.Max(markPower, spacePower);
                var minPower = Math.Min(markPower, spacePower);

                // Need strong enough signal with clear frequency dominance
                if (maxPower < profile.SignalThreshold ||
                    (minPower > 0 && maxPower / minPower < profile.DecisionRatio))
                {
                    valid = false;
                    break;
                }

                var bit = markPower > spacePower;
                if (prevBit.HasValue && bit != prevBit.Value)
                {
                    alternations++;
                }

                prevBit = bit;
            }

            // Preamble has perfect alternation (7 out of 7 transitions for 8 bits)
            // Allow 1 miss for noise robustness
            if (valid && alternations >= windowSize - 2)
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
