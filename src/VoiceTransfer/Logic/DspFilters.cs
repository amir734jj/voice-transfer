using VoiceTransfer.Data;

namespace VoiceTransfer.Logic;

/// <summary>
/// DSP preprocessing for received audio.
///
/// Over-the-air transmission introduces: broadband room noise, speaker/mic
/// frequency response coloring, DC offset, variable gain. These filters
/// clean up the received signal before it reaches the Goertzel demodulator.
///
/// Pipeline: DC removal → Bandpass filter → Peak normalization
/// </summary>
public static class DspFilters
{
    /// <summary>
    /// Full preprocessing chain for received audio.
    /// Applies DC removal, bandpass filtering around FSK frequencies,
    /// and peak normalization. Returns a new array (non-destructive).
    /// </summary>
    public static float[] Preprocess(float[] samples, TransmissionProfile profile)
    {
        var result = new float[samples.Length];
        Array.Copy(samples, result, samples.Length);

        RemoveDc(result);

        // Bandpass: keep only the FSK frequency band with margin proportional to separation
        var freqLow = Math.Min(profile.FreqMark, profile.FreqSpace);
        var freqHigh = Math.Max(profile.FreqMark, profile.FreqSpace);
        var margin = (freqHigh - freqLow) * 0.5; // half the separation as margin
        BandpassFilter(result, freqLow - margin, freqHigh + margin, Constants.SampleRate);

        Normalize(result);

        return result;
    }

    /// <summary>
    /// Live/interactive preprocessing: DC removal + bandpass only, no normalization.
    /// In live mode, normalization amplifies ambient noise to look like FSK signal,
    /// causing false detections. Working with actual signal levels lets the adaptive
    /// threshold properly distinguish real FSK from silence.
    /// </summary>
    public static float[] PreprocessLive(float[] samples, TransmissionProfile profile)
    {
        var result = new float[samples.Length];
        Array.Copy(samples, result, samples.Length);

        RemoveDc(result);

        var freqLow = Math.Min(profile.FreqMark, profile.FreqSpace);
        var freqHigh = Math.Max(profile.FreqMark, profile.FreqSpace);
        var margin = (freqHigh - freqLow) * 0.5;
        BandpassFilter(result, freqLow - margin, freqHigh + margin, Constants.SampleRate);

        return result;
    }

    /// <summary>
    /// Remove DC offset by subtracting the mean.
    /// Microphones and ADCs commonly introduce small DC bias that shifts
    /// the signal away from zero, affecting power estimates.
    /// </summary>
    public static void RemoveDc(float[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        double sum = 0;
        for (var i = 0; i < samples.Length; i++)
            sum += samples[i];

        var dc = (float)(sum / samples.Length);
        for (var i = 0; i < samples.Length; i++)
            samples[i] -= dc;
    }

    /// <summary>
    /// Peak-normalize audio to [-1, 1].
    /// Makes absolute thresholds meaningful regardless of mic gain or distance.
    /// </summary>
    public static void Normalize(float[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        var peak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var abs = Math.Abs(samples[i]);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        if (peak < 1e-10f)
        {
            return; // silence -- don't amplify noise floor
        }

        var scale = 1f / peak;
        for (var i = 0; i < samples.Length; i++)
            samples[i] *= scale;
    }

    /// <summary>
    /// Apply a 2nd-order Butterworth bandpass filter (cascaded high-pass + low-pass).
    /// Rejects out-of-band noise: room rumble, AC hum, high-frequency hiss,
    /// speaker harmonics -- all of which corrupt Goertzel power estimates.
    ///
    /// Uses standard Audio EQ Cookbook biquad formulas (Robert Bristow-Johnson).
    /// Applied forward-backward (zero-phase) to avoid group delay distortion
    /// that could shift bit boundaries.
    /// </summary>
    public static void BandpassFilter(float[] samples, double lowCutoff, double highCutoff, int sampleRate)
    {
        if (samples.Length < 4)
        {
            return;
        }

        // Clamp to valid range
        lowCutoff = Math.Max(20, lowCutoff);
        highCutoff = Math.Min(sampleRate / 2.0 - 1, highCutoff);

        // High-pass to remove everything below the FSK band
        ApplyBiquadZeroPhase(samples, DesignHighPass(lowCutoff, sampleRate));

        // Low-pass to remove everything above the FSK band
        ApplyBiquadZeroPhase(samples, DesignLowPass(highCutoff, sampleRate));
    }

    /// <summary>
    /// 2nd-order Butterworth high-pass biquad coefficients.
    /// </summary>
    private static BiquadCoeffs DesignHighPass(double cutoffHz, int sampleRate)
    {
        var w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        var cosw0 = Math.Cos(w0);
        var sinw0 = Math.Sin(w0);
        var alpha = sinw0 / (2.0 * Math.Sqrt(0.5)); // Q = 1/sqrt(2) for Butterworth

        var a0 = 1.0 + alpha;
        return new BiquadCoeffs
        {
            B0 = (1.0 + cosw0) / 2.0 / a0,
            B1 = -(1.0 + cosw0) / a0,
            B2 = (1.0 + cosw0) / 2.0 / a0,
            A1 = -2.0 * cosw0 / a0,
            A2 = (1.0 - alpha) / a0,
        };
    }

    /// <summary>
    /// 2nd-order Butterworth low-pass biquad coefficients.
    /// </summary>
    private static BiquadCoeffs DesignLowPass(double cutoffHz, int sampleRate)
    {
        var w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        var cosw0 = Math.Cos(w0);
        var sinw0 = Math.Sin(w0);
        var alpha = sinw0 / (2.0 * Math.Sqrt(0.5));

        var a0 = 1.0 + alpha;
        return new BiquadCoeffs
        {
            B0 = (1.0 - cosw0) / 2.0 / a0,
            B1 = (1.0 - cosw0) / a0,
            B2 = (1.0 - cosw0) / 2.0 / a0,
            A1 = -2.0 * cosw0 / a0,
            A2 = (1.0 - alpha) / a0,
        };
    }

    /// <summary>
    /// Apply a biquad filter forward then backward (zero-phase filtering).
    /// Forward-only filtering introduces group delay that shifts different
    /// frequencies by different amounts -- catastrophic for bit-boundary alignment.
    /// Forward-backward cancels the phase response, preserving timing.
    /// </summary>
    private static void ApplyBiquadZeroPhase(float[] samples, BiquadCoeffs c)
    {
        // Forward pass
        ApplyBiquad(samples, c, forward: true);
        // Backward pass (time-reversed)
        ApplyBiquad(samples, c, forward: false);
    }

    private static void ApplyBiquad(float[] samples, BiquadCoeffs c, bool forward)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;

        int start, end, step;
        if (forward)
        {
            start = 0;
            end = samples.Length;
            step = 1;
        }
        else
        {
            start = samples.Length - 1;
            end = -1;
            step = -1;
        }

        for (var i = start; i != end; i += step)
        {
            double x0 = samples[i];
            var y0 = c.B0 * x0 + c.B1 * x1 + c.B2 * x2 - c.A1 * y1 - c.A2 * y2;

            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;

            samples[i] = (float)y0;
        }
    }

    private struct BiquadCoeffs
    {
        public double B0, B1, B2, A1, A2;
    }
}
