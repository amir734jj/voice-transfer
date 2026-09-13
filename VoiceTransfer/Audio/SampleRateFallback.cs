namespace VoiceTransfer.Audio;

internal readonly record struct SampleRateOpenResult<T>(T Value, double SampleRate);

internal static class SampleRateFallback
{
    public static SampleRateOpenResult<T> Open<T>(
        double requestedRate,
        double fallbackRate,
        Func<double, T> open,
        Func<Exception, bool> canFallback)
    {
        try
        {
            return new SampleRateOpenResult<T>(open(requestedRate), requestedRate);
        }
        catch (Exception exception) when (
            Math.Abs(fallbackRate - requestedRate) > 0.5 && canFallback(exception))
        {
            return new SampleRateOpenResult<T>(open(fallbackRate), fallbackRate);
        }
    }
}