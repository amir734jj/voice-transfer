using System.Text;
using NAudio.Wave;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Interactive receiver: continuously captures audio, detects FSK frames
/// in real-time, and prints decoded text to the console.
///
/// Two input modes:
///   1. Microphone (default): records from mic, passes audio through to
///      speakers so you hear the caller, and extracts FSK in background.
///   2. Loopback (--loopback): captures system audio output directly via
///      WASAPI loopback -- ideal for same-machine testing where sender and
///      receiver run on the same PC without a physical mic/speaker path.
/// </summary>
public static class InteractiveReceiver
{
    public static void Run(int inputDevice, int outputDevice, TransmissionProfile profile, bool loopback = false, string? password = null)
    {
        profile.LogSettings();

        Log.Information("Interactive receiver listening...");
        Log.Information("Ctrl+C to quit.");
        Log.Information("");

        // Ring buffer for FSK processing
        var bufferCapacity = Constants.SampleRate * 120;
        var ringBuffer = new float[bufferCapacity];
        var writePos = 0;
        var lastProcessedEnd = 0;
        var bufferLock = new object();

        // Shared callback: feed mono float samples into the ring buffer
        void FeedRingBuffer(float[] samples)
        {
            lock (bufferLock)
            {
                CompactIfNeeded(ringBuffer, ref writePos, ref lastProcessedEnd, bufferCapacity, samples.Length);
                var count = Math.Min(samples.Length, bufferCapacity - writePos);
                Array.Copy(samples, 0, ringBuffer, writePos, count);
                writePos += count;
            }
        }

        IWaveIn waveIn;
        WaveOutEvent? waveOut = null;

        if (loopback)
        {
            var capture = new WasapiLoopbackCapture();
            waveIn = capture;
            var fmt = capture.WaveFormat;
            Log.Information("WASAPI loopback mode (capturing system audio output)");
            Log.Information("Capture format: {Rate}Hz, {Bits}-bit, {Ch}ch",
                fmt.SampleRate, fmt.BitsPerSample, fmt.Channels);

            capture.DataAvailable += (_, e) =>
            {
                var samples = ResampleToMono(e.Buffer, e.BytesRecorded, fmt);
                if (samples.Length > 0) FeedRingBuffer(samples);
            };
        }
        else
        {
            var inFormat = new WaveFormat(Constants.SampleRate, 16, Constants.Channels);
            var waveInEvent = new WaveInEvent
            {
                DeviceNumber = inputDevice,
                WaveFormat = inFormat,
                BufferMilliseconds = 100
            };
            waveIn = waveInEvent;

            // Audio passthrough so user hears the caller
            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);
            var passthrough = new BufferedWaveProvider(outFormat)
            {
                BufferLength = Constants.SampleRate * 4 * 5,
                ReadFully = true,
                DiscardOnBufferOverflow = true
            };
            waveOut = new WaveOutEvent
            {
                DeviceNumber = outputDevice,
                DesiredLatency = 150
            };
            waveOut.Init(passthrough);
            waveOut.Play();

            Log.Information("Audio passthrough: input [{In}] -> output [{Out}]", inputDevice, outputDevice);

            waveInEvent.DataAvailable += (_, e) =>
            {
                var sampleCount = e.BytesRecorded / 2;

                // Convert 16-bit PCM to float and pass through to speakers
                var monoSamples = new float[sampleCount];
                var floatBuf = new byte[sampleCount * 4];
                for (var i = 0; i < sampleCount; i++)
                {
                    var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                    monoSamples[i] = sample;
                    BitConverter.TryWriteBytes(floatBuf.AsSpan(i * 4), sample);
                }
                passthrough.AddSamples(floatBuf, 0, floatBuf.Length);

                FeedRingBuffer(monoSamples);
            };
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Log.Information("Shutting down receiver...");
        };

        waveIn.StartRecording();

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
                        continue;
                    if (currentWritePos - lastProcessedEnd < Constants.SampleRate)
                        continue;

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
                            lastProcessedEnd = currentWritePos - keepSamples;
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
            waveIn.StopRecording();
            waveIn.Dispose();
            waveOut?.Stop();
            waveOut?.Dispose();
        }

        Log.Information("Receiver stopped. Decoded {Count} messages.", messageCount);
    }

    private static void CompactIfNeeded(float[] ringBuffer, ref int writePos, ref int lastProcessedEnd, int capacity, int incoming)
    {
        if (writePos + incoming < capacity) return;
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
    /// Convert raw WASAPI capture bytes to mono float samples, resampled to our target rate.
    /// Handles various bit depths, channel counts, and sample rate differences.
    /// </summary>
    private static float[] ResampleToMono(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = bytesPerSample * format.Channels;
        var frameCount = bytesRecorded / frameSize;
        if (frameCount == 0) return [];

        var ratio = (double)Constants.SampleRate / format.SampleRate;
        var outFrames = (int)(frameCount * ratio);
        var result = new float[outFrames];

        for (var i = 0; i < outFrames; i++)
        {
            var srcPos = i / ratio;
            var srcIdx = Math.Min((int)srcPos, frameCount - 1);
            var frac = srcPos - srcIdx;

            var s0 = ReadMonoFrame(buffer, srcIdx, format, bytesPerSample, frameSize);
            var s1 = srcIdx + 1 < frameCount
                ? ReadMonoFrame(buffer, srcIdx + 1, format, bytesPerSample, frameSize) : s0;
            result[i] = (float)(s0 + (s1 - s0) * frac);
        }

        return result;
    }

    private static float ReadMonoFrame(byte[] buffer, int frameIndex, WaveFormat format, int bytesPerSample, int frameSize)
    {
        var offset = frameIndex * frameSize;

        float sum = 0;
        for (var ch = 0; ch < format.Channels; ch++)
        {
            var pos = offset + ch * bytesPerSample;
            if (pos + bytesPerSample > buffer.Length) break;

            var sample = format.BitsPerSample switch
            {
                32 when format.Encoding == WaveFormatEncoding.IeeeFloat =>
                    BitConverter.ToSingle(buffer, pos),
                16 => BitConverter.ToInt16(buffer, pos) / 32768f,
                _ => 0
            };
            sum += sample;
        }
        return sum / format.Channels;
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
            if (offset < 0) continue;

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
