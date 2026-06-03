using global::NAudio.Wave;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.NAudio;

internal sealed class NAudioDuplex : IAudioDuplex
{
    private readonly WaveInEvent _waveIn;
    private readonly WaveOutEvent _waveOut;
    private readonly BufferedWaveProvider _playBuffer;
    private readonly ChunkedAudioBuffer _buffer = new();

    public NAudioDuplex(int inputDeviceIndex, int outputDeviceIndex)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = inputDeviceIndex,
            WaveFormat = format,
            BufferMilliseconds = 20
        };
        _waveIn.DataAvailable += (_, e) =>
        {
            var floats = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
            _buffer.Enqueue(floats);
        };

        _playBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
        _waveOut = new WaveOutEvent { DeviceNumber = outputDeviceIndex };
        _waveOut.Init(_playBuffer);

        _waveIn.StartRecording();
        _waveOut.Play();
    }

    public int Read(Span<float> buffer) => _buffer.Read(buffer);

    public void Write(ReadOnlySpan<float> buffer)
    {
        var bytes = new byte[buffer.Length * 4];
        for (var i = 0; i < buffer.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), buffer[i]);
        _playBuffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveOut.Stop();
        _waveOut.Dispose();
    }
}
