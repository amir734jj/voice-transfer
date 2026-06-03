using NAudio.Wave;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio;

internal sealed class NAudioPlayer(int deviceIndex) : IAudioPlayer
{
    public void Play(float[] samples)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);
        var provider = new FloatArrayProvider(samples, format);

        using var waveOut = new WaveOutEvent { DeviceNumber = deviceIndex };
        using var done = new ManualResetEventSlim(false);
        waveOut.PlaybackStopped += (_, _) => done.Set();
        waveOut.Init(provider);
        waveOut.Play();
        done.Wait();
    }

    public void Dispose() { }
}