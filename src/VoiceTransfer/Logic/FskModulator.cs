using VoiceTransfer.Data;

namespace VoiceTransfer.Logic;

/// <summary>
/// Continuous-phase FSK modulator.
/// Converts a bit stream into audio samples using frequency-shift keying.
/// Mark (1) and Space (0) frequencies are configured via TransmissionProfile.
/// Phase is continuous across bit boundaries to avoid spectral splatter.
/// </summary>
public class FskModulator(TransmissionProfile profile)
{
    private double _phase = 0;

    /// <summary>
    /// Modulate a single bit into audio samples.
    /// </summary>
    private void ModulateBit(bool bit, float[] buffer, int offset)
    {
        var freq = bit ? profile.FreqMark : profile.FreqSpace;
        var phaseIncrement = 2.0 * Math.PI * freq / Constants.SampleRate;
        var samplesPerBit = profile.SamplesPerBit;

        for (var i = 0; i < samplesPerBit; i++)
        {
            // Smooth amplitude envelope: 5% fade in/out to reduce click artifacts
            var envelope = 1.0;
            var fadeLen = samplesPerBit / 20; // 5%
            if (fadeLen > 0)
            {
                if (i < fadeLen)
                {
                    envelope = (double)i / fadeLen;
                }
                else if (i >= samplesPerBit - fadeLen)
                {
                    envelope = (double)(samplesPerBit - 1 - i) / fadeLen;
                }
            }

            buffer[offset + i] = (float)(profile.Amplitude * envelope * Math.Sin(_phase));
            _phase += phaseIncrement;
        }

        // Keep phase in [0, 2pi) to avoid floating-point drift
        _phase %= (2.0 * Math.PI);
    }

    /// <summary>
    /// Modulate an array of bits into audio samples.
    /// </summary>
    public float[] ModulateBits(bool[] bits)
    {
        var samplesPerBit = profile.SamplesPerBit;
        var samples = new float[bits.Length * samplesPerBit];
        for (var i = 0; i < bits.Length; i++)
        {
            ModulateBit(bits[i], samples, i * samplesPerBit);
        }
        return samples;
    }

    /// <summary>
    /// Concatenate multiple sample arrays.
    /// </summary>
    public static float[] Concat(params float[][] arrays)
    {
        var total = 0;
        foreach (var a in arrays) total += a.Length;

        var result = new float[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            Array.Copy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }
}
