using System.Text;
using Ownaudio.Core;
using OwnaudioNET;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Interactive receiver: continuously captures audio, detects FSK frames
/// in real-time, and prints decoded text to the console.
///
/// Records from mic, passes audio through to speakers so you hear the
/// caller, and extracts FSK data in the background.
/// </summary>
public static class InteractiveReceiver
{
    public static void Run(int inputDevice, int outputDevice, TransmissionProfile profile, string? password = null)
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

        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            EnableOutput = true,
            EnableInput = true
        };

        var inputs = OwnaudioNet.GetInputDevices();
        if (inputDevice < inputs.Count)
        {
            config.InputDeviceId = inputs[inputDevice].DeviceId;
        }

        var outputs = OwnaudioNet.GetOutputDevices();
        if (outputDevice < outputs.Count)
        {
            config.OutputDeviceId = outputs[outputDevice].DeviceId;
        }

        Log.Information("Audio passthrough: input [{In}] -> output [{Out}]", inputDevice, outputDevice);

        OwnaudioNet.Initialize(config);
        OwnaudioNet.Start();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Log.Information("Shutting down receiver...");
        };

        // I/O thread: receive from mic, pass through to speakers, feed ring buffer
        var ioThread = new Thread(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var buffer = OwnaudioNet.Receive(out var sampleCount);
                if (buffer != null && sampleCount > 0)
                {
                    // Passthrough to speakers
                    OwnaudioNet.Send(buffer.AsSpan(0, sampleCount));

                    // Feed ring buffer for FSK processing
                    lock (bufferLock)
                    {
                        CompactIfNeeded(ringBuffer, ref writePos, ref lastProcessedEnd, bufferCapacity, sampleCount);
                        var count = Math.Min(sampleCount, bufferCapacity - writePos);
                        Array.Copy(buffer, 0, ringBuffer, writePos, count);
                        writePos += count;
                    }

                    OwnaudioNet.ReturnInputBuffer(buffer);
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

                    if (currentWritePos - lastProcessedEnd < Constants.SampleRate)
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

                var decoded = ReceiverMode.DemodulateAndDecode(snapshot, profile, password);
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
                    var keepSamples = Constants.SampleRate * 10;
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
            OwnaudioNet.Shutdown();
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

        Log.Debug("FSK power: mark={Mark:E2} space={Space:E2} threshold={Thresh:E2} [{Strength}]",
            maxMark, maxSpace, profile.SignalThreshold, strength);
    }
}
