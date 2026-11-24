using NAudio.Wave;
using LibreFlow.Services;

namespace LibreFlow.Audio;

internal sealed record Recording(byte[] Wav, double Seconds, float PeakLevel);

/// <summary>Microphone capture as 16 kHz mono 16-bit, the format speech models resample to anyway.</summary>
internal sealed class Recorder : IDisposable
{
    public const int SampleRate = 16000;

    private readonly object _gate = new();
    private WaveInEvent? _wave;
    private MemoryStream? _pcm;
    private TaskCompletionSource? _stopped;
    private float _peak;

    /// <summary>Loudness of the latest audio block, 0..1. Raised on the audio thread.</summary>
    public event Action<float>? Level;

    public bool IsRecording => _wave is not null;

    public static List<string> DeviceNames()
    {
        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++) names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return names;
    }

    public void Start(string preferredDevice)
    {
        Cancel();
        var device = -1; // Windows default microphone
        if (!string.IsNullOrEmpty(preferredDevice))
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                if (WaveInEvent.GetCapabilities(i).ProductName == preferredDevice) device = i;
        }

        var wave = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 30,
        };
        lock (_gate)
        {
            _pcm = new MemoryStream(SampleRate * 2 * 30);
            _peak = 0;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        wave.DataAvailable += OnData;
        wave.RecordingStopped += (_, _) => _stopped?.TrySetResult();
        wave.StartRecording();
        _wave = wave;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        double sum = 0;
        var samples = e.BytesRecorded / 2;
        for (var i = 0; i < e.BytesRecorded - 1; i += 2)
        {
            var sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8)) / 32768.0;
            sum += sample * sample;
        }
        var rms = samples > 0 ? (float)Math.Sqrt(sum / samples) : 0f;
        lock (_gate)
        {
            _pcm?.Write(e.Buffer, 0, e.BytesRecorded);
            if (rms > _peak) _peak = rms;
        }
        Level?.Invoke(rms);
    }

    public async Task<Recording?> StopAsync()
    {
        var wave = _wave;
        if (wave is null) return null;
        _wave = null;
        var stopped = _stopped;
        try
        {
            wave.StopRecording();
            if (stopped is not null) await Task.WhenAny(stopped.Task, Task.Delay(1500));
        }
        catch (Exception ex)
        {
            Log.Error("Error al detener la grabación", ex);
        }
        finally
        {
            wave.Dispose();
        }

        byte[] pcm;
        float peak;
        lock (_gate)
        {
            pcm = _pcm?.ToArray() ?? [];
            peak = _peak;
            _pcm = null;
        }
        return new Recording(ToWav(pcm), pcm.Length / 2.0 / SampleRate, peak);
    }

    public void Cancel()
    {
        var wave = _wave;
        _wave = null;
        if (wave is null) return;
        try
        {
            wave.StopRecording();
        }
        catch
        {
            // Already stopped.
        }
        wave.Dispose();
        lock (_gate) _pcm = null;
    }

    public static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Length in seconds of a WAV produced by <see cref="ToWav"/>.</summary>
    public static double WavSeconds(byte[] wav) => Math.Max(0, wav.Length - 44) / 2.0 / SampleRate;

    public void Dispose() => Cancel();
}
