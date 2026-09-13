using VoiceTransfer.Audio;
using VoiceTransfer.Data;
using Xunit;

namespace VoiceTransfer.Tests;

public sealed class PortAudioFallbackTests : AcousticChannelTestBase
{
    private const double DeviceSampleRate = 44100;

    [Fact]
    public void Open_When48KhzFails_RetriesAtDeviceSampleRate()
    {
        var attemptedRates = new List<double>();

        var result = SampleRateFallback.Open(
            Constants.SampleRate,
            DeviceSampleRate,
            sampleRate =>
            {
                attemptedRates.Add(sampleRate);
                if (sampleRate == Constants.SampleRate)
                {
                    throw new InvalidOperationException("Unsupported sample rate");
                }

                return new object();
            },
            exception => exception is InvalidOperationException);

        Assert.Equal([Constants.SampleRate, DeviceSampleRate], attemptedRates);
        Assert.Equal(DeviceSampleRate, result.SampleRate);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public void RobustProfile_DecodesAfter44Point1KhzCaptureFallback()
    {
        var payload = "sample rate fallback round trip"u8.ToArray();
        var microphoneSamples = CreateMicrophoneRecording(
            payload, clockErrorPpm: 0, AmbientNoise.Room);
        var deviceSamples = new StreamingLinearResampler(
            Constants.SampleRate, DeviceSampleRate).Process(microphoneSamples);
        var captureResampler = new StreamingLinearResampler(
            DeviceSampleRate, Constants.SampleRate);
        var resampledCapture = new List<float>();

        const int callbackFrames = 317;
        for (var offset = 0; offset < deviceSamples.Length; offset += callbackFrames)
        {
            var count = Math.Min(callbackFrames, deviceSamples.Length - offset);
            resampledCapture.AddRange(captureResampler.Process(
                deviceSamples.AsSpan(offset, count)));
        }

        var outputPath = GetArtifactPath("acoustic-44100-fallback.wav");
        var decoded = DecodeThroughWav(outputPath, resampledCapture.ToArray());

        Assert.Equal(payload, decoded);
    }
}