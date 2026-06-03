using System.Runtime.InteropServices;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;
using AudioFormat = SoundFlow.Structs.AudioFormat;

namespace VoiceTransfer.Audio.SoundFlow;

public sealed class SoundFlowBackend : IAudioBackend
{
    public IReadOnlyList<string> GetInputDeviceNames()
    {
        using var engine = new MiniAudioEngine();
        engine.UpdateAudioDevicesInfo();
        return engine.CaptureDevices.Select(d => d.Name).ToList();
    }

    public IReadOnlyList<string> GetOutputDeviceNames()
    {
        using var engine = new MiniAudioEngine();
        engine.UpdateAudioDevicesInfo();
        return engine.PlaybackDevices.Select(d => d.Name).ToList();
    }

    public bool SupportsLoopback => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public IAudioPlayer CreatePlayer(int outputDeviceIndex) =>
        new SoundFlowPlayer(outputDeviceIndex);

    public IAudioRecorder CreateRecorder(int inputDeviceIndex) =>
        new SoundFlowRecorder(inputDeviceIndex);

    public IAudioRecorder? CreateLoopbackRecorder()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        return new SoundFlowLoopbackRecorder();
    }

    public IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex) =>
        new SoundFlowDuplex(inputDeviceIndex, outputDeviceIndex);

    internal static AudioFormat MakeFormat() => new()
    {
        SampleRate = Constants.SampleRate,
        Channels = Constants.Channels,
        Format = SampleFormat.F32,
        Layout = ChannelLayout.Mono
    };
}

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

/// <summary>
/// Minimal SoundComponent that feeds float samples directly into the mixer's GenerateAudio path.
/// Avoids the SoundPlayer/DataProvider layer which has format conversion issues.
/// </summary>
internal sealed class FloatBufferSource : SoundComponent
{
    private readonly float[] _samples;
    private readonly Action _onComplete;
    private int _position;

    public FloatBufferSource(AudioEngine engine, AudioFormat format, float[] samples, Action onComplete)
        : base(engine, format)
    {
        _samples = samples;
        _onComplete = onComplete;
    }

    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        var remaining = _samples.Length - _position;
        if (remaining <= 0)
        {
            buffer.Clear();
            _onComplete();
            return;
        }

        // For mono source into potentially multi-channel output,
        // write one sample per frame, repeat across channels
        var frames = buffer.Length / channels;
        var toCopy = Math.Min(frames, remaining);

        for (var i = 0; i < toCopy; i++)
        {
            var sample = _samples[_position++];
            for (var ch = 0; ch < channels; ch++)
            {
                buffer[i * channels + ch] = sample;
            }
        }

        // Zero-fill remaining frames
        for (var i = toCopy * channels; i < buffer.Length; i++)
        {
            buffer[i] = 0;
        }

        if (_position >= _samples.Length)
        {
            _onComplete();
        }
    }
}

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
