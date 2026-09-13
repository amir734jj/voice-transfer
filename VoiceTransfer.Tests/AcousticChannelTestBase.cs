using VoiceTransfer.Data;
using VoiceTransfer.Logic;
using VoiceTransfer.Modes;
using Xunit;

namespace VoiceTransfer.Tests;

public abstract class AcousticChannelTestBase
{
    private static readonly TransmissionProfile Profile = TransmissionProfile.FromOptions(
        "robust", null, null, null, null, null, null);

    protected static byte[]? DecodeThroughWav(string path, float[] microphoneSamples)
    {
        WavFile.Write(path, microphoneSamples, Constants.SampleRate, Constants.Channels);
        var fromFile = WavFile.Read(path, out var sampleRate, out var channels);

        Assert.Equal(Constants.SampleRate, sampleRate);
        Assert.Equal(Constants.Channels, channels);

        var processed = DspFilters.Preprocess(fromFile, Profile);
        return ReceiverMode.DemodulateAndDecode(
            processed, Profile, strictDetection: true);
    }

    protected static float[] CreateMicrophoneRecording(
        byte[] payload, int clockErrorPpm, AmbientNoise ambientNoise)
    {
        var bits = FrameCodec.Encode(payload, Profile);
        var transmitted = new FskModulator(Profile).ModulateBits(bits);
        var padded = AddSilence(transmitted, 0.5, 0.75);
        var colored = ApplySpeakerAndMicrophoneResponse(padded);
        var reverberant = AddRoomEcho(colored);
        var drifted = ResampleForClockError(reverberant, clockErrorPpm);
        return AddAmbientNoise(drifted, ambientNoise, seed: 734);
    }

    protected static float[] CreateHardwareLimitedRecording(byte[] payload)
    {
        var bits = FrameCodec.Encode(payload, Profile);
        var transmitted = new FskModulator(Profile).ModulateBits(bits);
        var padded = AddSilence(transmitted, 0.5, 0.75);

        var speakerEq = ApplyPeakingEq(
            padded, Profile.FreqMark, gainDb: -6, q: 1.5);
        var speakerLimited = ApplySoftKneeCompressor(
            speakerEq, thresholdDb: -12, ratio: 8, kneeDb: 6);
        var reverberant = AddRoomEcho(speakerLimited);
        var microphoneEq = ApplyPeakingEq(
            reverberant, Profile.FreqMark, gainDb: -6, q: 1.5);
        var microphoneLimited = ApplySoftKneeCompressor(
            microphoneEq, thresholdDb: -18, ratio: 10, kneeDb: 8);

        return AddAmbientNoise(microphoneLimited, AmbientNoise.Room, seed: 912);
    }

    private static float[] AddSilence(float[] samples, double leadingSeconds, double trailingSeconds)
    {
        var leading = (int)(leadingSeconds * Constants.SampleRate);
        var trailing = (int)(trailingSeconds * Constants.SampleRate);
        var result = new float[leading + samples.Length + trailing];
        Array.Copy(samples, 0, result, leading, samples.Length);
        return result;
    }

    private static float[] ApplySpeakerAndMicrophoneResponse(float[] samples)
    {
        var result = new float[samples.Length];
        var previousInput = 0f;
        var previousOutput = 0f;

        for (var i = 0; i < samples.Length; i++)
        {
            var highPassed = samples[i] - previousInput + 0.985f * previousOutput;
            previousInput = samples[i];
            previousOutput = highPassed;
            result[i] = MathF.Tanh(highPassed * 1.8f) * 0.35f;
        }

        return result;
    }

    private static float[] AddRoomEcho(float[] samples)
    {
        var result = new float[samples.Length];
        var firstDelay = (int)(0.007 * Constants.SampleRate);
        var secondDelay = (int)(0.019 * Constants.SampleRate);

        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            if (i >= firstDelay)
            {
                value += samples[i - firstDelay] * 0.32f;
            }

            if (i >= secondDelay)
            {
                value += samples[i - secondDelay] * 0.16f;
            }

            result[i] = Math.Clamp(value, -1f, 1f);
        }

        return result;
    }

    private static float[] ApplyPeakingEq(
        float[] samples, double centerFrequency, double gainDb, double q)
    {
        var amplitude = Math.Pow(10.0, gainDb / 40.0);
        var omega = 2.0 * Math.PI * centerFrequency / Constants.SampleRate;
        var alpha = Math.Sin(omega) / (2.0 * q);
        var cosOmega = Math.Cos(omega);
        var a0 = 1.0 + alpha / amplitude;
        var b0 = (1.0 + alpha * amplitude) / a0;
        var b1 = -2.0 * cosOmega / a0;
        var b2 = (1.0 - alpha * amplitude) / a0;
        var a1 = -2.0 * cosOmega / a0;
        var a2 = (1.0 - alpha / amplitude) / a0;
        var result = new float[samples.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            var x0 = samples[i];
            var y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
            result[i] = (float)y0;
        }

        return result;
    }

    private static float[] ApplySoftKneeCompressor(
        float[] samples, double thresholdDb, double ratio, double kneeDb)
    {
        var result = new float[samples.Length];
        var attackCoefficient = Math.Exp(-1.0 / (0.002 * Constants.SampleRate));
        var releaseCoefficient = Math.Exp(-1.0 / (0.080 * Constants.SampleRate));
        double envelope = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            var level = Math.Abs(samples[i]);
            var coefficient = level > envelope ? attackCoefficient : releaseCoefficient;
            envelope = coefficient * envelope + (1.0 - coefficient) * level;
            var levelDb = 20.0 * Math.Log10(Math.Max(envelope, 1e-9));
            var gainDb = CompressorGainDb(levelDb, thresholdDb, ratio, kneeDb);
            result[i] = (float)(samples[i] * Math.Pow(10.0, gainDb / 20.0));
        }

        return result;
    }

    private static double CompressorGainDb(
        double levelDb, double thresholdDb, double ratio, double kneeDb)
    {
        var lowerKnee = thresholdDb - kneeDb / 2.0;
        var upperKnee = thresholdDb + kneeDb / 2.0;
        if (levelDb <= lowerKnee)
        {
            return 0;
        }

        if (levelDb >= upperKnee)
        {
            var compressedDb = thresholdDb + (levelDb - thresholdDb) / ratio;
            return compressedDb - levelDb;
        }

        var distanceIntoKnee = levelDb - lowerKnee;
        return (1.0 / ratio - 1.0) * distanceIntoKnee * distanceIntoKnee
            / (2.0 * kneeDb);
    }

    private static float[] ResampleForClockError(float[] samples, int clockErrorPpm)
    {
        var ratio = 1.0 + clockErrorPpm / 1_000_000.0;
        var result = new float[(int)Math.Round(samples.Length * ratio)];

        for (var i = 0; i < result.Length; i++)
        {
            var sourcePosition = i / ratio;
            var left = Math.Min((int)sourcePosition, samples.Length - 1);
            var right = Math.Min(left + 1, samples.Length - 1);
            var fraction = (float)(sourcePosition - left);
            result[i] = samples[left] + (samples[right] - samples[left]) * fraction;
        }

        return result;
    }

    private static float[] AddAmbientNoise(float[] samples, AmbientNoise ambientNoise, int seed)
    {
        var signalPower = samples.Select(sample => (double)sample * sample).Average();
        var signalToNoiseDb = ambientNoise == AmbientNoise.WhiteNoise ? 3.0 : 6.0;
        var targetNoisePower = signalPower / Math.Pow(10.0, signalToNoiseDb / 10.0);
        var random = new Random(seed);
        var noise = new double[samples.Length];
        double fanState = 0;
        double trafficState = 0;

        for (var i = 0; i < noise.Length; i++)
        {
            var time = (double)i / Constants.SampleRate;
            var white = NextGaussian(random);
            fanState = fanState * 0.995 + white * 0.005;
            trafficState = trafficState * 0.9995 + white * 0.0005;

            var sample = 0.0;
            if (ambientNoise.HasFlag(AmbientNoise.WhiteNoise))
            {
                sample += white;
            }

            if (ambientNoise.HasFlag(AmbientNoise.Room))
            {
                sample += white * 0.25
                    + Math.Sin(2.0 * Math.PI * 60.0 * time)
                    + 0.35 * Math.Sin(2.0 * Math.PI * 120.0 * time);
            }

            if (ambientNoise.HasFlag(AmbientNoise.FanAndHvac))
            {
                sample += fanState * 12.0
                    + 0.8 * Math.Sin(2.0 * Math.PI * 60.0 * time)
                    + 0.25 * Math.Sin(2.0 * Math.PI * 180.0 * time);
            }

            if (ambientNoise.HasFlag(AmbientNoise.SpeechBabble))
            {
                sample += CreateSpeechBabble(time, white);
            }

            if (ambientNoise.HasFlag(AmbientNoise.HumanTalk))
            {
                sample += CreateHumanTalk(time, white);
            }

            if (ambientNoise.HasFlag(AmbientNoise.Traffic))
            {
                sample += trafficState * 35.0
                    + white * 0.12
                    + 0.6 * Math.Sin(2.0 * Math.PI * 45.0 * time)
                    + 0.2 * Math.Sin(2.0 * Math.PI * 90.0 * time);
            }

            noise[i] = sample;
        }

        var measuredNoisePower = noise.Select(value => value * value).Average();
        var noiseScale = Math.Sqrt(targetNoisePower / measuredNoisePower);
        var result = new float[samples.Length];

        for (var i = 0; i < samples.Length; i++)
        {
            result[i] = Math.Clamp(samples[i] + (float)(noise[i] * noiseScale), -1f, 1f);
        }

        return result;
    }

    private static double NextGaussian(Random random)
    {
        var u1 = Math.Max(random.NextDouble(), double.Epsilon);
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double CreateSpeechBabble(double time, double breathNoise)
    {
        var syllableEnvelope = 0.45
            + 0.25 * Math.Sin(2.0 * Math.PI * 3.7 * time)
            + 0.20 * Math.Sin(2.0 * Math.PI * 5.3 * time + 1.2);

        var voices = Math.Sin(2.0 * Math.PI * 180.0 * time)
            + 0.6 * Math.Sin(2.0 * Math.PI * 720.0 * time + 0.4)
            + 0.35 * Math.Sin(2.0 * Math.PI * 1450.0 * time + 1.1)
            + 0.2 * Math.Sin(2.0 * Math.PI * 2850.0 * time + 2.0);

        return syllableEnvelope * voices + breathNoise * 0.15;
    }

    private static double CreateHumanTalk(double time, double breathNoise)
    {
        var syllableEnvelope = Math.Max(0.0,
            0.35
            + 0.35 * Math.Sin(2.0 * Math.PI * 4.2 * time)
            + 0.20 * Math.Sin(2.0 * Math.PI * 2.1 * time + 0.8));
        var pitch = 125.0 + 18.0 * Math.Sin(2.0 * Math.PI * 0.7 * time);
        var voiced = Math.Sin(2.0 * Math.PI * pitch * time)
            + 0.55 * Math.Sin(2.0 * Math.PI * pitch * 2.0 * time)
            + 0.3 * Math.Sin(2.0 * Math.PI * 750.0 * time)
            + 0.2 * Math.Sin(2.0 * Math.PI * 1200.0 * time);

        return syllableEnvelope * voiced + breathNoise * 0.08;
    }

    protected static string GetArtifactPath(string fileName)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "VoiceTransfer.slnx")))
        {
            root = root.Parent;
        }

        var directory = Path.Combine(root?.FullName ?? AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    [Flags]
    public enum AmbientNoise
    {
        WhiteNoise = 1,
        Room = 2,
        FanAndHvac = 4,
        SpeechBabble = 8,
        HumanTalk = 16,
        Traffic = 32,
        All = WhiteNoise | Room | FanAndHvac | SpeechBabble | HumanTalk | Traffic,
    }
}