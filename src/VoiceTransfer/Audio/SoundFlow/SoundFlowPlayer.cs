using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.SoundFlow;

internal sealed class SoundFlowPlayer : IAudioPlayer
{
    private readonly MiniAudioEngine _engine;
    private readonly AudioPlaybackDevice _device;
    private readonly AudioFormat _format;

    public SoundFlowPlayer(int outputDeviceIndex)
    {
        _engine = new MiniAudioEngine();
        _format = SoundFlowBackend.MakeFormat();

        // Initialize device with stereo format since most hardware devices require stereo.
        // Our mono samples get upmixed to stereo in GenerateAudio via the channels parameter.
        var deviceFormat = new AudioFormat
        {
            SampleRate = Constants.SampleRate,
            Channels = 2,
            Format = SampleFormat.F32,
            Layout = ChannelLayout.Stereo
        };

        _engine.UpdateAudioDevicesInfo();
        var deviceInfo = outputDeviceIndex < _engine.PlaybackDevices.Length
            ? _engine.PlaybackDevices[outputDeviceIndex]
            : (DeviceInfo?)null;

        _device = _engine.InitializePlaybackDevice(deviceInfo, deviceFormat);
        _device.Start();
    }

    public void Play(float[] samples)
    {
        using var done = new ManualResetEventSlim(false);
        var source = new FloatBufferSource(_engine, _format, samples, () => done.Set());

        _device.MasterMixer.AddComponent(source);

        done.Wait();

        _device.MasterMixer.RemoveComponent(source);
        source.Dispose();
    }

    public void Dispose()
    {
        _device.Stop();
        _device.Dispose();
        _engine.Dispose();
    }
}