using Xunit;

namespace VoiceTransfer.Tests;

public sealed class AcousticChannelTests : AcousticChannelTestBase
{
    public static TheoryData<AmbientNoise> AmbientNoiseCombinations
    {
        get
        {
            var combinations = new TheoryData<AmbientNoise>();
            for (var value = 1; value <= (int)AmbientNoise.All; value++)
            {
                combinations.Add((AmbientNoise)value);
            }

            return combinations;
        }
    }

    [Fact]
    public void RobustProfile_DecodesThroughNoisyEchoingRoom_AndDumpsWav()
    {
        var payload = "acoustic round trip"u8.ToArray();
        var microphoneSamples = CreateMicrophoneRecording(
            payload, clockErrorPpm: 0, AmbientNoise.WhiteNoise);
        var outputPath = GetArtifactPath("acoustic-noise-echo.wav");

        var decoded = DecodeThroughWav(outputPath, microphoneSamples);

        Assert.Equal(payload, decoded);
    }

    [Theory]
    [MemberData(nameof(AmbientNoiseCombinations))]
    public void RobustProfile_DecodesThroughAmbientNoiseCombinations(AmbientNoise ambientNoise)
    {
        var payload = "natural noise round trip"u8.ToArray();
        var microphoneSamples = CreateMicrophoneRecording(
            payload, clockErrorPpm: 0, ambientNoise);
        var combinationName = ambientNoise.ToString().Replace(", ", "-");
        var outputPath = GetArtifactPath($"acoustic-{combinationName}.wav");

        var decoded = DecodeThroughWav(outputPath, microphoneSamples);

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RobustProfile_DecodesThroughFrequencyCapsAndDeviceLimiters()
    {
        var payload = "limited speaker and microphone"u8.ToArray();
        var microphoneSamples = CreateHardwareLimitedRecording(payload);
        var outputPath = GetArtifactPath("acoustic-frequency-caps-limiters.wav");

        var decoded = DecodeThroughWav(outputPath, microphoneSamples);

        Assert.Equal(payload, decoded);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(-150)]
    public void RobustProfile_DecodesWithIndependentAudioClocks(int clockErrorPpm)
    {
        var payload = "Clock drift accumulates across this message."u8.ToArray();
        var microphoneSamples = CreateMicrophoneRecording(
            payload, clockErrorPpm, AmbientNoise.Room);
        var outputPath = GetArtifactPath($"acoustic-clock-{clockErrorPpm:+0;-0}.wav");

        var decoded = DecodeThroughWav(outputPath, microphoneSamples);

        Assert.Equal(payload, decoded);
    }
}