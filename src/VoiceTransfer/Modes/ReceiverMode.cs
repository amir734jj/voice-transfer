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

        // Diagnostic: show Goertzel power at FSK frequencies across the recording
        LogSignalDiagnostics(samples, profile);

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
    public static byte[]? DemodulateAndDecode(float[] samples, TransmissionProfile profile, string? password = null, bool skipFullScan = false, bool strictDetection = false)
    {
        Log.Debug("Step 1: Signal detection...");
        var demod = new FskDemodulator(profile);

        // Find approximate start of signal (first block with energy at FSK frequencies)
        var signalStart = FindSignalStart(samples, profile, strictDetection);
        if (signalStart >= 0)
        {
            Log.Information("Signal detected at sample {Start} ({Time:F3}s)",
                signalStart, (double)signalStart / Constants.SampleRate);

            // Calibrate frequency gain to compensate for uneven speaker/mic/codec response
            Log.Debug("Calibrating frequency gain...");
            demod.CalibrateFromSignal(samples, signalStart, samples.Length);

            // Find best bit alignment using preamble correlation
            Log.Debug("Step 2: Bit synchronization via preamble...");
            var searchLen = Math.Min(profile.PreambleBits * profile.SamplesPerBit * 2, samples.Length - signalStart);
            var alignedStart = demod.FindBestAlignment(samples, signalStart, searchLen);

            Log.Debug("Aligned to sample {Start} ({Time:F3}s)", alignedStart, (double)alignedStart / Constants.SampleRate);

            // Try the best alignment and a few nearby offsets (hard + soft)
            for (var nudge = -profile.SamplesPerBit / 2; nudge <= profile.SamplesPerBit / 2; nudge += profile.SamplesPerBit / 8)
            {
                var tryStart = alignedStart + nudge;
                if (tryStart < 0)
                {
                    continue;
                }

                var result = TryDemodulate(samples, tryStart, demod, profile.FecRepeat, password)
                          ?? TryDemodulateSoft(samples, tryStart, demod, profile.FecRepeat, password);
                if (result != null)
                {
                    Log.Debug("Successful decode at offset {Offset} (nudge={Nudge})", tryStart, nudge);
                    return result;
                }
            }

            // Step 2b: Try expected data position directly (reverb may corrupt sync byte)
            // The sync byte is often destroyed by preamble reverb over-the-air,
            // so try decoding from the expected position: preamble + 8 bits.
            Log.Debug("Step 2b: Trying expected data position (bypassing sync byte)...");
            for (var preambleOffset = -30; preambleOffset <= 30; preambleOffset++)
            {
                var expectedDataBit = profile.PreambleBits + 8 + preambleOffset;
                for (var nudge = -profile.SamplesPerBit / 2; nudge <= profile.SamplesPerBit / 2; nudge += profile.SamplesPerBit / 4)
                {
                    var sampleOffset = alignedStart + nudge;
                    if (sampleOffset < 0)
                    {
                        continue;
                    }

                    var dataStartSample = sampleOffset + expectedDataBit * profile.SamplesPerBit;
                    if (dataStartSample >= samples.Length)
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
                        Log.Information("Decoded via expected position (preambleOffset={Offset}, nudge={Nudge})",
                            preambleOffset, nudge);
                        return result;
                    }

                    // Also try soft decode
                    var softBits = demod.DemodulateAllSoft(samples, sampleOffset);
                    if (expectedDataBit >= softBits.Length)
                    {
                        continue;
                    }

                    result = FrameCodec.DecodeFromPositionSoft(softBits, expectedDataBit, profile.FecRepeat, password);
                    if (result != null)
                    {
                        Log.Information("Decoded via expected position soft (preambleOffset={Offset}, nudge={Nudge})",
                            preambleOffset, nudge);
                        return result;
                    }
                }
            }

            // Brute force: try every single-sample offset in a bit-width window
            Log.Debug("Step 3: Brute-force alignment search...");
            for (var offset = Math.Max(0, signalStart - profile.SamplesPerBit);
                 offset < signalStart + profile.SamplesPerBit * 2 && offset < samples.Length;
                 offset++)
            {
                var result = TryDemodulate(samples, offset, demod, profile.FecRepeat, password)
                          ?? TryDemodulateSoft(samples, offset, demod, profile.FecRepeat, password);
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
        // Skipped in live/interactive mode where it's too expensive and would
        // block reception of the next message.
        if (skipFullScan)
        {
            return null;
        }

        Log.Debug("Step 4: Full-scan fallback (scanning entire audio)...");
        var stepSize = profile.SamplesPerBit / 2; // half-bit resolution
        for (var offset = 0; offset < samples.Length - profile.SamplesPerBit * 16; offset += stepSize)
        {
            var result = TryDemodulate(samples, offset, demod, profile.FecRepeat, password)
                      ?? TryDemodulateSoft(samples, offset, demod, profile.FecRepeat, password);
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
            var result = FrameCodec.Decode(bits, fecRepeat, password);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Soft-decision variant of TryDemodulate. Uses log-likelihood ratios
    /// instead of hard bits for much better FEC error correction over-the-air.
    /// </summary>
    private static byte[]? TryDemodulateSoft(float[] samples, int startOffset, FskDemodulator demod, int fecRepeat, string? password)
    {
        var softBits = demod.DemodulateAllSoft(samples, startOffset);
        if (softBits.Length < 16)
        {
            return null;
        }

        return FrameCodec.DecodeSoft(softBits, fecRepeat, password);
    }

    /// <summary>
    /// Find the sample index where FSK signal first appears.
    /// Uses adaptive thresholding: computes the noise floor from the quietest portion,
    /// then finds the first block where combined FSK power jumps significantly above noise.
    /// Does NOT require alternation (the channel may distort one frequency, making
    /// alternation invisible before calibration).
    /// </summary>
    private static int FindSignalStart(float[] samples, TransmissionProfile profile, bool strictDetection = false)
    {
        var blockSize = profile.SamplesPerBit;
        var numBlocks = samples.Length / blockSize;

        if (numBlocks < 8)
        {
            return numBlocks > 0 ? 0 : -1;
        }

        // Step 1: Compute per-block total FSK power (mark + space)
        var blockPowers = new double[numBlocks];
        for (var i = 0; i < numBlocks; i++)
        {
            var offset = i * blockSize;
            if (offset + blockSize > samples.Length)
            {
                break;
            }

            var mp = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqMark);
            var sp = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqSpace);
            blockPowers[i] = mp + sp;
        }

        // Step 2: Compute adaptive threshold.
        var sorted = blockPowers.OrderBy(p => p).ToArray();

        double adaptiveThreshold;
        double noiseFloor;

        if (strictDetection)
        {
            // Mic mode: use 5th percentile × 20.
            // Mic captures continuously so the buffer always has ambient noise
            // blocks. The 5th percentile reliably reflects noise even when
            // most of the buffer is signal.
            var idx = Math.Max(0, sorted.Length / 20);
            noiseFloor = sorted[idx];
            adaptiveThreshold = Math.Max(profile.SignalThreshold, noiseFloor * 20);
        }
        else
        {
            // Loopback/batch: use 10% of the 90th-percentile block power.
            // WASAPI loopback only captures when audio plays, so the buffer
            // may contain NO silence blocks at all — percentile-based noise
            // floor estimates would return signal-level values.
            // Using a fraction of the peak instead works regardless of the
            // silence/signal ratio in the buffer.
            var highIdx = sorted.Length * 9 / 10;
            var highPower = sorted[Math.Min(highIdx, sorted.Length - 1)];
            noiseFloor = highPower;
            adaptiveThreshold = Math.Max(profile.SignalThreshold, highPower * 0.1);
        }

        Log.Information("Adaptive threshold: {Threshold:E2} (ref={Floor:E2}, strict={Strict})",
            adaptiveThreshold, noiseFloor, strictDetection);

        // Step 3: Find first run of consecutive blocks above threshold.
        // Strict mode (mic): scale with preamble length to reject false triggers.
        //   Robust (512-bit preamble): 32 consecutive = ~640ms sustained signal.
        //   Normal (128-bit preamble): 8 consecutive.
        // Non-strict mode (loopback/batch): lenient (3-4 blocks).
        var requiredConsecutive = strictDetection
            ? Math.Max(8, profile.PreambleBits / 16)
            : (profile.BaudRate <= 100 ? 3 : 4);
        var consecutive = 0;

        for (var i = 0; i < numBlocks; i++)
        {
            if (blockPowers[i] >= adaptiveThreshold)
            {
                consecutive++;
                if (consecutive >= requiredConsecutive)
                {
                    return (i - requiredConsecutive + 1) * blockSize;
                }
            }
            else
            {
                consecutive = 0;
            }
        }

        return -1;
    }

    /// <summary>
    /// Log diagnostic information about FSK signal presence across the recording.
    /// Shows power levels at mark/space frequencies and mark/space ratio in 0.5s chunks.
    /// </summary>
    private static void LogSignalDiagnostics(float[] samples, TransmissionProfile profile)
    {
        var blockSize = profile.SamplesPerBit;
        var chunkDuration = 0.5; // seconds per diagnostic chunk
        var samplesPerChunk = (int)(chunkDuration * Constants.SampleRate);
        var numChunks = samples.Length / samplesPerChunk;

        Log.Information("Signal diagnostics (mark={Mark}Hz, space={Space}Hz):", profile.FreqMark, profile.FreqSpace);

        var anySignal = false;
        for (var c = 0; c < numChunks; c++)
        {
            var chunkStart = c * samplesPerChunk;
            var blocksInChunk = samplesPerChunk / blockSize;

            double maxMark = 0, maxSpace = 0;
            var validBlocks = 0;

            for (var b = 0; b < blocksInChunk; b++)
            {
                var offset = chunkStart + b * blockSize;
                if (offset + blockSize > samples.Length)
                {
                    break;
                }

                var markPower = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqMark);
                var spacePower = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqSpace);

                if (markPower > maxMark)
                {
                    maxMark = markPower;
                }

                if (spacePower > maxSpace)
                {
                    maxSpace = spacePower;
                }

                var maxP = Math.Max(markPower, spacePower);
                var minP = Math.Min(markPower, spacePower);
                if (maxP >= profile.SignalThreshold && (minP == 0 || maxP / minP >= profile.DecisionRatio))
                {
                    validBlocks++;
                }
            }

            var pct = blocksInChunk > 0 ? 100.0 * validBlocks / blocksInChunk : 0;
            if (maxMark > 0.0001 || maxSpace > 0.0001)
            {
                anySignal = true;
            }

            Log.Information("  {Time:F1}s: markPwr={Mark:E2}, spacePwr={Space:E2}, valid={Valid}/{Total} ({Pct:F0}%)",
                c * chunkDuration, maxMark, maxSpace, validBlocks, blocksInChunk, pct);
        }

        if (!anySignal)
        {
            Log.Warning("No significant FSK energy detected at either frequency. " +
                         "The audio may not contain an FSK signal, or it may be at different frequencies.");
        }
    }

    public static float[] ReadWavPublic(string path) => ReadWav(path);

    private static float[] ReadWav(string path)
    {
        if (!File.Exists(path))
        {
            Log.Error("Audio file not found: {File}", path);
            return Array.Empty<float>();
        }

        // Use WavFile reader for .wav, MediaFoundation for everything else (mp4, m4a, 3gp, etc.)
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var samples = WavFile.Read(path, out var sampleRate, out var channels);
            Log.Information("Reading WAV: {Rate}Hz, {Ch}ch, {Duration:F1}s",
                sampleRate, channels, (double)samples.Length / sampleRate);
            return samples;
        }

        return ReadMediaFoundation(path);
    }

    /// <summary>
    /// Read any audio format by converting to WAV via ffmpeg, then reading the WAV.
    /// Falls back to NAudio MediaFoundation if ffmpeg is not available.
    /// </summary>
    private static float[] ReadMediaFoundation(string path)
    {
        // Try ffmpeg first (works everywhere, no COM dependency)
        var ffmpegResult = TryConvertWithFfmpeg(path);
        if (ffmpegResult != null)
        {
            return ffmpegResult;
        }

        // Fallback: NAudio MediaFoundation (Windows only, needs COM)
        try
        {
            using var reader = new NAudio.Wave.MediaFoundationReader(path);
            var targetFormat = new NAudio.Wave.WaveFormat(Constants.SampleRate, 16, 1);
            using var resampler = new NAudio.Wave.MediaFoundationResampler(reader, targetFormat);
            resampler.ResamplerQuality = 60;

            var allBytes = new List<byte>();
            var buffer = new byte[targetFormat.AverageBytesPerSecond];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                allBytes.AddRange(buffer.AsSpan(0, bytesRead).ToArray());
            }

            var samples = new float[allBytes.Count / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToInt16(allBytes.ToArray(), i * 2) / 32768f;
            }

            Log.Information("Reading {Ext}: {Rate}Hz, mono, {Duration:F1}s (via MediaFoundation)",
                Path.GetExtension(path).ToUpper(), Constants.SampleRate,
                (double)samples.Length / Constants.SampleRate);
            return samples;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read audio file: {Error}", ex.Message);
            Log.Error("Install ffmpeg (winget install Gyan.FFmpeg) or use a .wav file");
            return Array.Empty<float>();
        }
    }

    /// <summary>
    /// Convert any audio file to mono 48kHz PCM WAV via ffmpeg, then read the WAV.
    /// Returns null if ffmpeg is not available.
    /// </summary>
    private static float[]? TryConvertWithFfmpeg(string inputPath)
    {
        var tempWav = Path.Combine(Path.GetTempPath(), $"vt-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{inputPath}\" -ar {Constants.SampleRate} -ac 1 -sample_fmt s16 -y \"{tempWav}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            proc.WaitForExit(30_000);
            if (!proc.HasExited || proc.ExitCode != 0)
            {
                Log.Debug("ffmpeg conversion failed (exit code {Code})", proc.HasExited ? proc.ExitCode : -1);
                return null;
            }

            var samples = WavFile.Read(tempWav, out var sampleRate, out var channels);
            Log.Information("Reading {Ext}: {Rate}Hz, {Ch}ch, {Duration:F1}s (via ffmpeg)",
                Path.GetExtension(inputPath).ToUpper(), sampleRate, channels,
                (double)samples.Length / sampleRate);
            return samples;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ffmpeg not found on PATH
            Log.Debug("ffmpeg not found on PATH, falling back to MediaFoundation");
            return null;
        }
        finally
        {
            if (File.Exists(tempWav))
            {
                File.Delete(tempWav);
            }
        }
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
