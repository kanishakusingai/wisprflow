using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace FreeFlow.Windows.Services;

public sealed record MicrophoneInfo(int DeviceNumber, string Name);

/// <summary>
/// Records the microphone as 16 kHz mono 16-bit PCM (what Whisper wants) into memory and
/// reports a smoothed 0..1 loudness level for the Flow Bar waveform.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const int SampleRate = 16_000;
    private WaveInEvent? _waveIn;
    private MemoryStream? _pcm;
    private float _smoothedLevel;

    /// <summary>Raised on a background thread roughly 20 times a second.</summary>
    public event Action<float>? LevelChanged;

    public bool IsRecording => _waveIn != null;
    public TimeSpan RecordedDuration => _pcm == null ? TimeSpan.Zero : TimeSpan.FromSeconds(_pcm.Length / 2.0 / SampleRate);

    public static IReadOnlyList<MicrophoneInfo> ListMicrophones()
    {
        var list = new List<MicrophoneInfo>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            list.Add(new MicrophoneInfo(i, WaveInEvent.GetCapabilities(i).ProductName));
        }
        return list;
    }

    /// <param name="deviceNumber">-1 for the Windows default microphone.</param>
    public void Start(int deviceNumber)
    {
        if (_waveIn != null) return;
        if (WaveInEvent.DeviceCount == 0)
        {
            throw new InvalidOperationException("No microphone found. Plug one in or check Windows sound settings.");
        }

        if (deviceNumber >= WaveInEvent.DeviceCount) deviceNumber = -1;

        _pcm = new MemoryStream();
        _smoothedLevel = 0;
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _pcm?.Write(e.Buffer, 0, e.BytesRecorded);

        // RMS loudness of this buffer
        double sum = 0;
        var samples = e.BytesRecorded / 2;
        for (var i = 0; i < e.BytesRecorded - 1; i += 2)
        {
            var s = BitConverter.ToInt16(e.Buffer, i) / 32768.0;
            sum += s * s;
        }
        var rms = samples > 0 ? Math.Sqrt(sum / samples) : 0;

        // Map to a lively 0..1 range: speech RMS is usually ~0.02-0.2.
        var level = (float)Math.Clamp(Math.Pow(rms * 6.0, 0.7), 0, 1);
        // Fast attack, slower release, like a VU meter.
        _smoothedLevel = level > _smoothedLevel
            ? _smoothedLevel + (level - _smoothedLevel) * 0.6f
            : _smoothedLevel + (level - _smoothedLevel) * 0.25f;
        LevelChanged?.Invoke(_smoothedLevel);
    }

    /// <summary>Stops recording and returns a complete WAV file, or null if nothing was recorded.</summary>
    public byte[]? Stop()
    {
        var waveIn = _waveIn;
        var pcm = _pcm;
        _waveIn = null;
        _pcm = null;
        if (waveIn == null || pcm == null) return null;

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.StopRecording();
        waveIn.Dispose();

        if (pcm.Length == 0) return null;
        return BuildWav(pcm.ToArray());
    }

    private static byte[] BuildWav(byte[] pcm)
    {
        var ms = new MemoryStream();
        // Disposing the writer finalizes the WAV header (and closes ms, which ToArray tolerates).
        using (var writer = new WaveFileWriter(ms, new WaveFormat(SampleRate, 16, 1)))
        {
            writer.Write(pcm, 0, pcm.Length);
        }
        return ms.ToArray();
    }

    public void Dispose() => Stop();
}
