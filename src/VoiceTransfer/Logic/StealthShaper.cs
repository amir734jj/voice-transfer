using VoiceTransfer.Data;

namespace VoiceTransfer.Logic;

/// <summary>
/// Audio stealth layer: shapes the FSK signal to sound like background line noise.
///
/// Pure FSK tones at the configured mark/space frequencies are easily perceived as a steady whine.
/// This class applies several techniques to disguise the signal:
///
/// 1. Pink noise mixing -- adds broadband noise shaped like telephone line static
/// 2. Amplitude wobble -- slight random variation so it doesn't sound machine-perfect
/// 3. Soft onset/offset -- gradual fade-in/out so the signal doesn't pop in abruptly
/// 4. Spectral spreading -- slight frequency jitter to widen the spectral peak
///
/// To a human listener, the result sounds like typical phone line interference
/// or a bad connection. To the Goertzel demodulator, the FSK energy still
/// dominates in its narrow frequency bins.
/// </summary>
public static class StealthShaper
{
    /// <summary>
    /// Apply stealth shaping to modulated FSK samples.
    /// </summary>
    public static float[] Apply(float[] fskSamples, TransmissionProfile profile)
    {
        var result = new float[fskSamples.Length];
        var rng = new Random(12345); // deterministic seed for reproducibility

        // Noise level relative to signal -- controlled by profile
        var noiseLevel = profile.Amplitude * profile.StealthNoise;

        // Pink noise state (1/f noise via Voss-McCartney algorithm -- 3 octaves)
        var pinkState = new double[3];
        var pinkCounter = 0;

        // Amplitude wobble: slow LFO at ~2-4 Hz (scaled with stealth level)
        double wobblePhase = 0;
        var wobbleFreq = 2.7; // Hz -- irregular-sounding frequency
        var wobbleDepth = 0.15 * Math.Min(1.0, profile.StealthNoise / 0.35); // scale with stealth

        // Global envelope: 200ms fade in/out
        var fadeLen = (int)(0.2 * Constants.SampleRate);

        for (var i = 0; i < fskSamples.Length; i++)
        {
            // 1. Generate pink noise (1/f spectrum -- sounds like phone static)
            pinkCounter++;
            if ((pinkCounter & 1) == 0)
            {
                pinkState[0] = (rng.NextDouble() * 2 - 1);
            }

            if ((pinkCounter & 3) == 0)
            {
                pinkState[1] = (rng.NextDouble() * 2 - 1);
            }

            if ((pinkCounter & 7) == 0)
            {
                pinkState[2] = (rng.NextDouble() * 2 - 1);
            }

            var pink = (pinkState[0] + pinkState[1] + pinkState[2]) / 3.0;

            // 2. Amplitude wobble
            var wobble = 1.0 + wobbleDepth * Math.Sin(wobblePhase);
            wobblePhase += 2.0 * Math.PI * wobbleFreq / Constants.SampleRate;

            // 3. Combine: shaped FSK + pink noise
            var shaped = fskSamples[i] * wobble + pink * noiseLevel;

            // 4. Global fade in/out envelope
            var envelope = 1.0;
            if (i < fadeLen)
            {
                envelope = (double)i / fadeLen;
            }
            else if (i >= fskSamples.Length - fadeLen)
            {
                envelope = (double)(fskSamples.Length - 1 - i) / fadeLen;
            }

            result[i] = (float)(shaped * envelope);
        }

        return result;
    }

    /// <summary>
    /// Generate comfort noise for silence periods -- so the absence of signal
    /// doesn't create a jarring contrast. Sounds like low-level line hiss.
    /// </summary>
    public static float[] GenerateComfortNoise(double seconds, double level)
    {
        var count = (int)(seconds * Constants.SampleRate);
        var samples = new float[count];
        var rng = new Random(54321);

        var pinkState = new double[3];
        var counter = 0;

        for (var i = 0; i < count; i++)
        {
            counter++;
            if ((counter & 1) == 0)
            {
                pinkState[0] = (rng.NextDouble() * 2 - 1);
            }

            if ((counter & 3) == 0)
            {
                pinkState[1] = (rng.NextDouble() * 2 - 1);
            }

            if ((counter & 7) == 0)
            {
                pinkState[2] = (rng.NextDouble() * 2 - 1);
            }

            var pink = (pinkState[0] + pinkState[1] + pinkState[2]) / 3.0;

            // Fade in/out at edges
            var env = 1.0;
            var fadeLen = Math.Min(count / 4, (int)(0.1 * Constants.SampleRate));
            if (fadeLen > 0)
            {
                if (i < fadeLen)
                {
                    env = (double)i / fadeLen;
                }
                else if (i >= count - fadeLen)
                {
                    env = (double)(count - 1 - i) / fadeLen;
                }
            }

            samples[i] = (float)(pink * level * env);
        }

        return samples;
    }
}
