using System.Text;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Interactive receiver: continuously captures audio, detects FSK frames
/// in real-time, and prints decoded text to the console.
///
/// Records from mic, passes audio through to speakers so you hear the
/// caller, and extracts FSK data in the background.
/// In loopback mode, captures system audio output directly (Windows only).
/// </summary>
public static class InteractiveReceiver
{
    public static void Run(IAudioBackend audio, int inputDevice, int outputDevice, TransmissionProfile profile, bool loopback = false, bool passthrough = false, string? password = null)
    {
        profile.LogSettings();

        Log.Information("Interactive receiver listening...");
        Log.Information("Ctrl+C to quit");
        Log.Information("");

        // Ring buffer for FSK processing
        var bufferCapacity = Constants.SampleRate * 120;
        var ringBuffer = new float[bufferCapacity];
        var writePos = 0;
        var lastProcessedEnd = 0;
        var bufferLock = new object();

        IDisposable? session;
        Func<float[], int> readFunc;
        Action<float[], int>? passthroughFunc = null;

        if (loopback)
        {
            Log.Information("Loopback mode: capturing system audio output (WASAPI)");
            var recorder = audio.CreateLoopbackRecorder()
                ?? throw new PlatformNotSupportedException("Loopback not supported by current audio backend");
            session = recorder;
            readFunc = buf => recorder.Read(buf);
        }
        else if (passthrough)
        {
            Log.Information("Audio passthrough: input [{In}] -> output [{Out}] (use headphones to avoid feedback)", inputDevice, outputDevice);
            var duplex = audio.CreateDuplex(inputDevice, outputDevice);
            session = duplex;
            readFunc = buf => duplex.Read(buf);
            passthroughFunc = (buf, count) => duplex.Write(buf.AsSpan(0, count));
        }
        else
        {
            Log.Information("Recording from input device [{In}]", inputDevice);
            var recorder = audio.CreateRecorder(inputDevice);
            session = recorder;
            readFunc = buf => recorder.Read(buf);
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Log.Information("Shutting down receiver...");
        };

        // I/O thread: capture audio, optional passthrough, feed ring buffer
        var readBuffer = new float[4096];
        var ioThread = new Thread(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var sampleCount = readFunc(readBuffer);
                if (sampleCount > 0)
                {
                    // Passthrough to speakers (non-loopback only)
                    passthroughFunc?.Invoke(readBuffer, sampleCount);

                    // Feed ring buffer for FSK processing
                    lock (bufferLock)
                    {
                        CompactIfNeeded(ringBuffer, ref writePos, ref lastProcessedEnd, bufferCapacity, sampleCount);
                        var count = Math.Min(sampleCount, bufferCapacity - writePos);
                        Array.Copy(readBuffer, 0, ringBuffer, writePos, count);
                        writePos += count;
                    }
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        })
        {
            IsBackground = true
        };

        ioThread.Start();

        var processIntervalMs = 500;
        var messageCount = 0;
        var diagCounter = 0;

        // Calculate minimum buffer retention based on profile.
        // Must keep enough audio to contain a full message:
        // preamble + sync + max reasonable payload with FEC.
        // At 50 baud with 512-bit preamble and 7x FEC, a short message is ~24s.
        var preambleSamples = profile.SamplesPerBit * (profile.PreambleBits + 8);
        var maxPayloadBits = 200 * 8 * profile.FecRepeat; // 200 bytes max payload
        var maxMessageSamples = preambleSamples + maxPayloadBits * profile.SamplesPerBit;
        var keepSamples = Math.Max(Constants.SampleRate * 30, maxMessageSamples + Constants.SampleRate * 5);

        // Minimum new audio before attempting decode.
        // At 50 baud, each bit is 960 samples; need at least a few bits to be useful.
        var minNewSamples = Math.Max(Constants.SampleRate, profile.SamplesPerBit * 16);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(processIntervalMs);

                float[] snapshot;
                int currentWritePos;

                lock (bufferLock)
                {
                    currentWritePos = writePos;
                    if (currentWritePos <= lastProcessedEnd)
                    {
                        continue;
                    }

                    if (currentWritePos - lastProcessedEnd < minNewSamples)
                    {
                        continue;
                    }

                    var lookback = profile.SamplesPerBit * profile.PreambleBits;
                    var snapStart = Math.Max(0, lastProcessedEnd - lookback);
                    var snapLen = currentWritePos - snapStart;
                    snapshot = new float[snapLen];
                    Array.Copy(ringBuffer, snapStart, snapshot, 0, snapLen);
                }

                // Periodic diagnostics: show peak FSK power levels
                diagCounter++;
                if (diagCounter % 4 == 0)
                {
                    LogPeakPower(snapshot, profile);
                }

                // Preprocess: DC removal + bandpass for over-the-air robustness.
                // Skip normalization in live mode: normalization amplifies ambient
                // noise to look like signal, causing false detections and CPU-heavy
                // full-scan attempts. Real FSK from speakers has sufficient Goertzel
                // power even at low mic levels.
                var processed = DspFilters.PreprocessLive(snapshot, profile);

                var decoded = ReceiverMode.DemodulateAndDecode(processed, profile, password, skipFullScan: true, strictDetection: !loopback);
                if (decoded != null)
                {
                    messageCount++;
                    var text = Encoding.UTF8.GetString(decoded);
                    Console.WriteLine($"[{messageCount}] {text}");

                    lock (bufferLock)
                    {
                        lastProcessedEnd = currentWritePos;
                    }
                }
                else
                {
                    lock (bufferLock)
                    {
                        if (currentWritePos > keepSamples + Constants.SampleRate)
                        {
                            lastProcessedEnd = currentWritePos - keepSamples;
                        }
                    }
                }

                lock (bufferLock)
                {
                    if (writePos > bufferCapacity * 3 / 4)
                    {
                        var lookback = profile.SamplesPerBit * profile.PreambleBits;
                        var keepFrom = Math.Max(0, lastProcessedEnd - lookback);
                        var keep = writePos - keepFrom;
                        if (keepFrom > 0 && keep > 0 && keep < writePos)
                        {
                            Array.Copy(ringBuffer, keepFrom, ringBuffer, 0, keep);
                            writePos = keep;
                            lastProcessedEnd = Math.Max(0, lastProcessedEnd - keepFrom);
                        }
                    }
                }
            }
        }
        finally
        {
            cts.Cancel();
            ioThread.Join(2000);
            session.Dispose();
            cts.Dispose();
        }

        Log.Information("Receiver stopped. Decoded {Count} messages", messageCount);
    }

    private static void CompactIfNeeded(float[] ringBuffer, ref int writePos, ref int lastProcessedEnd, int capacity, int incoming)
    {
        if (writePos + incoming < capacity)
        {
            return;
        }

        var keep = Math.Min(writePos, capacity / 2);
        var discard = writePos - keep;
        if (discard > 0)
        {
            Array.Copy(ringBuffer, discard, ringBuffer, 0, keep);
            writePos = keep;
            lastProcessedEnd = Math.Max(0, lastProcessedEnd - discard);
        }
    }

    /// <summary>
    /// Log peak Goertzel power at FSK frequencies for diagnostics.
    /// Helps the user verify signal is reaching the receiver.
    /// </summary>
    private static void LogPeakPower(float[] samples, TransmissionProfile profile)
    {
        var blockSize = profile.SamplesPerBit;
        var numBlocks = Math.Min(samples.Length / blockSize, 100);
        double maxMark = 0, maxSpace = 0;

        for (var i = 0; i < numBlocks; i++)
        {
            var offset = samples.Length - numBlocks * blockSize + i * blockSize;
            if (offset < 0)
            {
                continue;
            }

            var mark = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqMark);
            var space = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqSpace);
            maxMark = Math.Max(maxMark, mark);
            maxSpace = Math.Max(maxSpace, space);
        }

        var maxPower = Math.Max(maxMark, maxSpace);
        var strength = maxPower > profile.SignalThreshold * 10 ? "STRONG"
                        : maxPower > profile.SignalThreshold ? "WEAK" : "NONE";

        Log.Information("FSK power: mark={Mark:E2} space={Space:E2} threshold={Thresh:E2} [{Strength}]",
            maxMark, maxSpace, profile.SignalThreshold, strength);
    }
}
