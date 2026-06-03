using global::NAudio.Wave;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.NAudio;

internal sealed class NAudioRecorder : IAudioRecorder
{
    private readonly WaveInEvent _waveIn;
    private readonly ChunkedAudioBuffer _buffer = new();

    public NAudioRecorder(int deviceIndex)
    {
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels),
            BufferMilliseconds = 20
        };
        _waveIn.DataAvailable += (_, e) =>
        {
            var floats = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
            _buffer.Enqueue(floats);
        };
        _waveIn.StartRecording();
    }

    public int Read(Span<float> buffer) => _buffer.Read(buffer);

    public void Dispose()
    {
        _waveIn.StopRecording();
        _waveIn.Dispose();
    }
}
