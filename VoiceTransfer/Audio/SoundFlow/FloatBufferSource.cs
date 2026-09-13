using SoundFlow.Abstracts;
using SoundFlow.Structs;

namespace VoiceTransfer.Audio.SoundFlow;

/// <summary>
/// Minimal SoundComponent that feeds float samples directly into the mixer's GenerateAudio path.
/// Avoids the SoundPlayer/DataProvider layer which has format conversion issues.
/// </summary>
internal sealed class FloatBufferSource(AudioEngine engine, AudioFormat format, float[] samples, Action onComplete)
    : SoundComponent(engine, format)
{
    private int _position;

    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        var remaining = samples.Length - _position;
        if (remaining <= 0)
        {
            buffer.Clear();
            onComplete();
            return;
        }

        // For mono source into potentially multi-channel output,
        // write one sample per frame, repeat across channels
        var frames = buffer.Length / channels;
        var toCopy = Math.Min(frames, remaining);

        for (var i = 0; i < toCopy; i++)
        {
            var sample = samples[_position++];
            for (var ch = 0; ch < channels; ch++)
            {
                buffer[i * channels + ch] = sample;
            }
        }

        // Zero-fill remaining frames
        for (var i = toCopy * channels; i < buffer.Length; i++)
        {
            buffer[i] = 0;
        }

        if (_position >= samples.Length)
        {
            onComplete();
        }
    }
}