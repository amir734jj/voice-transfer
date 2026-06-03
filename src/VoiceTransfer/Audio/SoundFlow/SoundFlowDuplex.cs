using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.SoundFlow;

internal sealed class SoundFlowDuplex : IAudioDuplex
{
    private readonly MiniAudioEngine _engine;
    private readonly FullDuplexDevice _device;
    private readonly AudioFormat _format;
    private readonly ChunkedAudioBuffer _buffer = new();
    private readonly QueueDataProvider _playQueue;
    private readonly SoundPlayer _player;

    public SoundFlowDuplex(int inputDeviceIndex, int outputDeviceIndex)
    {
        _engine = new MiniAudioEngine();
        _format = SoundFlowBackend.MakeFormat();

        _engine.UpdateAudioDevicesInfo();
        var captureInfo = inputDeviceIndex < _engine.CaptureDevices.Length
            ? _engine.CaptureDevices[inputDeviceIndex]
            : (DeviceInfo?)null;
        var playbackInfo = outputDeviceIndex < _engine.PlaybackDevices.Length
            ? _engine.PlaybackDevices[outputDeviceIndex]
            : (DeviceInfo?)null;

        _device = _engine.InitializeFullDuplexDevice(playbackInfo, captureInfo, _format);
        _device.CaptureDevice.OnAudioProcessed += OnAudio;

        // Set up playback queue for passthrough
        _playQueue = new QueueDataProvider(_format);
        _player = new SoundPlayer(_engine, _format, _playQueue);
        _device.MasterMixer.AddComponent(_player);
        _player.Play();

        _device.Start();
    }

    private void OnAudio(Span<float> samples, Capability _)
    {
        _buffer.Enqueue(samples.ToArray());
    }

    public int Read(Span<float> buffer) => _buffer.Read(buffer);

    public void Write(ReadOnlySpan<float> buffer)
    {
        _playQueue.AddSamples(buffer);
    }

    public void Dispose()
    {
        _device.CaptureDevice.OnAudioProcessed -= OnAudio;
        _device.MasterMixer.RemoveComponent(_player);
        _player.Stop();
        _player.Dispose();
        _playQueue.Dispose();
        _device.Stop();
        _device.Dispose();
        _engine.Dispose();
    }
}