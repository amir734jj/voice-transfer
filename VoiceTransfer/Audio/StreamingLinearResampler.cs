namespace VoiceTransfer.Audio;

internal sealed class StreamingLinearResampler(double sourceRate, double targetRate)
{
    private double _nextOutputSourcePosition;
    private long _sourceSamplesProcessed;
    private float _previousSourceSample;

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty)
        {
            return [];
        }

        var step = sourceRate / targetRate;
        var blockStart = _sourceSamplesProcessed;
        var blockEnd = blockStart + input.Length - 1;
        var output = new List<float>((int)Math.Ceiling(input.Length / step) + 1);

        while (_nextOutputSourcePosition <= blockEnd)
        {
            var leftIndex = (long)Math.Floor(_nextOutputSourcePosition);
            var fraction = (float)(_nextOutputSourcePosition - leftIndex);
            if (fraction > 0 && leftIndex >= blockEnd)
            {
                break;
            }

            var left = leftIndex < blockStart
                ? _previousSourceSample
                : input[(int)(leftIndex - blockStart)];
            var right = fraction == 0
                ? left
                : input[(int)(leftIndex + 1 - blockStart)];

            output.Add(left + (right - left) * fraction);
            _nextOutputSourcePosition += step;
        }

        _sourceSamplesProcessed += input.Length;
        _previousSourceSample = input[^1];
        return output.ToArray();
    }
}