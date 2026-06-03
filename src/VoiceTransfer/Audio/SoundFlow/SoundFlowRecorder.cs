using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.SoundFlow;

internal sealed class SoundFlowRecorder : IAudioRecorder
{
    private readonly MiniAudioEngine _engine;
    private readonly AudioCaptureDevice _device;
    private readonly ChunkedAudioBuffer _buffer = new();

    public SoundFlowRecorder(int inputDeviceIndex)
    {
        _engine = new MiniAudioEngine();
        var format = SoundFlowBackend.MakeFormat();

        _engine.UpdateAudioDevicesInfo();
        var deviceInfo = inputDeviceIndex < _engine.CaptureDevices.Length
            ? _engine.CaptureDevices[inputDeviceIndex]
            : (DeviceInfo?)null;

        _device = _engine.InitializeCaptureDevice(deviceInfo, format);
        _device.OnAudioProcessed += OnAudio;
        _device.Start();
    }

    private void OnAudio(Span<float> samples, Capability _)
    {
        _buffer.Enqueue(samples.ToArray());
    }

    public int Read(Span<float> buffer) => _buffer.Read(buffer);

    public void Dispose()
    {
        _device.OnAudioProcessed -= OnAudio;
        _device.Stop();
        _device.Dispose();
        _engine.Dispose();
    }
}