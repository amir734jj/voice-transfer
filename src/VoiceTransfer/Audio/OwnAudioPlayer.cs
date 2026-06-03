using Ownaudio.Core;
using OwnaudioNET;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio;

internal sealed class OwnAudioPlayer : IAudioPlayer
{
    private readonly AudioMixer _mixer;

    public OwnAudioPlayer(int outputDeviceIndex)
    {
        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            EnableOutput = true,
            EnableInput = false
        };

        var outputs = OwnaudioNet.GetOutputDevices();
        if (outputDeviceIndex < outputs.Count)
        {
            config.OutputDeviceId = outputs[outputDeviceIndex].DeviceId;
        }

        OwnaudioNet.Initialize(config);
        OwnaudioNet.Start();

        _mixer = new AudioMixer(OwnaudioNet.Engine!.UnderlyingEngine);
        _mixer.Start();
    }

    public void Play(float[] samples)
    {
        var source = new SampleSource(samples, OwnaudioNet.Engine!.Config);
        _mixer.AddSource(source);
        source.Play();

        while (!source.IsEndOfStream)
        {
            Thread.Sleep(50);
        }

        _mixer.RemoveSource(source);
        source.Dispose();
    }

    public void Dispose()
    {
        _mixer.Stop();
        _mixer.Dispose();
        OwnaudioNet.Shutdown();
    }
}