using System.Collections.Concurrent;
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

namespace VoiceTransfer.Audio;

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
            return null;
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

        _engine.UpdateAudioDevicesInfo();
        var deviceInfo = outputDeviceIndex < _engine.PlaybackDevices.Length
            ? _engine.PlaybackDevices[outputDeviceIndex]
            : (DeviceInfo?)null;

        _device = _engine.InitializePlaybackDevice(deviceInfo, _format);
        _device.Start();
    }

    public void Play(float[] samples)
    {
        using var provider = new RawDataProvider(samples, _format.SampleRate);
        using var player = new SoundPlayer(_engine, _format, provider);
        using var done = new ManualResetEventSlim(false);

        player.PlaybackEnded += (_, _) => done.Set();
        _device.MasterMixer.AddComponent(player);
        player.Play();
        done.Wait();
        _device.MasterMixer.RemoveComponent(player);
    }

    public void Dispose()
    {
        _device.Stop();
        _device.Dispose();
        _engine.Dispose();
    }
}

internal sealed class SoundFlowRecorder : IAudioRecorder
{
    private readonly MiniAudioEngine _engine;
    private readonly AudioCaptureDevice _device;
    private readonly ConcurrentQueue<float[]> _chunks = new();
    private float[] _current = [];
    private int _pos;

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
        _chunks.Enqueue(samples.ToArray());
    }

    public int Read(Span<float> buffer)
    {
        if (_pos >= _current.Length)
        {
            if (!_chunks.TryDequeue(out var next))
                return 0;
            _current = next;
            _pos = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsSpan(_pos, count).CopyTo(buffer);
        _pos += count;
        return count;
    }

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
    private readonly ConcurrentQueue<float[]> _chunks = new();
    private float[] _current = [];
    private int _pos;

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
        _chunks.Enqueue(samples.ToArray());
    }

    public int Read(Span<float> buffer)
    {
        if (_pos >= _current.Length)
        {
            if (!_chunks.TryDequeue(out var next))
                return 0;
            _current = next;
            _pos = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsSpan(_pos, count).CopyTo(buffer);
        _pos += count;
        return count;
    }

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
    private readonly ConcurrentQueue<float[]> _chunks = new();
    private float[] _current = [];
    private int _pos;
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
        _chunks.Enqueue(samples.ToArray());
    }

    public int Read(Span<float> buffer)
    {
        if (_pos >= _current.Length)
        {
            if (!_chunks.TryDequeue(out var next))
                return 0;
            _current = next;
            _pos = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsSpan(_pos, count).CopyTo(buffer);
        _pos += count;
        return count;
    }

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
