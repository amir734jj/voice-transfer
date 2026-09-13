using global::NAudio.CoreAudioApi;
using global::NAudio.Wave;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.NAudio;

internal sealed class NAudioPlayer(int deviceIndex, bool wasapiOut = false) : IAudioPlayer
{
    public void Play(float[] samples)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);
        var provider = new FloatArrayProvider(samples, format);
        using var done = new ManualResetEventSlim(false);

        IWavePlayer player;
        if (wasapiOut)
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            player = new WasapiOut(device, AudioClientShareMode.Shared, true, 150);
        }
        else
        {
            player = new WaveOutEvent
            {
                DeviceNumber = deviceIndex,
                DesiredLatency = 150,
                NumberOfBuffers = 3
            };
        }

        using (player)
        {
            player.PlaybackStopped += (_, args) =>
            {
                if (args.Exception != null)
                {
                    Log.Error(args.Exception, "Playback error");
                }
                done.Set();
            };
            if (wasapiOut)
            {
                player.Init(provider);
            }
            else
            {
                ((WaveOutEvent)player).Init(provider, convertTo16Bit: true);
            }
            player.Play();

            var durationMs = (int)(samples.Length * 1000.0 / Constants.SampleRate / Constants.Channels);
            done.Wait(durationMs + 2000);
        }
    }

    public void Dispose() { }
}
