using SoundFlow.Abstracts;
using SoundFlow.Structs;

namespace VoiceTransfer.Audio.SoundFlow;

/// <summary>
/// Minimal SoundComponent that feeds float samples directly into the mixer's GenerateAudio path.
/// Avoids the SoundPlayer/DataProvider layer which has format conversion issues.
/// </summary>
internal sealed class FloatBufferSource : SoundComponent
{
    private readonly float[] _samples;
    private readonly Action _onComplete;
    private int _position;

    public FloatBufferSource(AudioEngine engine, AudioFormat format, float[] samples, Action onComplete)
        : base(engine, format)
    {
        _samples = samples;
        _onComplete = onComplete;
    }

    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        var remaining = _samples.Length - _position;
        if (remaining <= 0)
        {
            buffer.Clear();
            _onComplete();
            return;
        }

        // For mono source into potentially multi-channel output,
        // write one sample per frame, repeat across channels
        var frames = buffer.Length / channels;
        var toCopy = Math.Min(frames, remaining);

        for (var i = 0; i < toCopy; i++)
        {
            var sample = _samples[_position++];
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

        if (_position >= _samples.Length)
        {
            _onComplete();
        }
    }
}