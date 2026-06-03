using global::NAudio.Wave;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.NAudio;

internal sealed class NAudioPlayer(int deviceIndex) : IAudioPlayer
{
    public void Play(float[] samples)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);
        var provider = new FloatArrayProvider(samples, format);
        using var done = new ManualResetEventSlim(false);

        var waveOut = new WaveOutEvent { DeviceNumber = deviceIndex };
        try
        {
            waveOut.PlaybackStopped += (_, _) => done.Set();
            waveOut.Init(provider);
            waveOut.Play();
            done.Wait();
        }
        finally
        {
            waveOut.Dispose();
        }
    }

    public void Dispose() { }
}
