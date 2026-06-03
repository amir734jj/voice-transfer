using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.SoundFlow;

internal sealed class SoundFlowLoopbackRecorder : IAudioRecorder
{
    private readonly MiniAudioEngine _engine;
    private readonly AudioCaptureDevice _device;
    private readonly ChunkedAudioBuffer _buffer = new();

    public SoundFlowLoopbackRecorder()
    {
        _engine = new MiniAudioEngine();
        var format = SoundFlowBackend.MakeFormat();

        _device = _engine.InitializeLoopbackDevice(format);
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