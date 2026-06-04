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

        using var waveOut = new WaveOutEvent
        {
            DeviceNumber = deviceIndex,
            DesiredLatency = 150,
            NumberOfBuffers = 3
        };

        waveOut.PlaybackStopped += (_, _) => done.Set();
        // Convert float samples to 16-bit PCM for universal waveOut compatibility
        waveOut.Init(provider, convertTo16Bit: true);
        waveOut.Play();

        var durationMs = (int)(samples.Length * 1000.0 / Constants.SampleRate / Constants.Channels);
        done.Wait(durationMs + 500);
    }

    public void Dispose() { }
}
