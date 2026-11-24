using System.Media;
using LibreFlow.Services;

namespace LibreFlow.Audio;

/// <summary>Short cues synthesized at startup, so the app ships no audio files.</summary>
internal sealed class Sounds
{
    private readonly SoundPlayer _start = Tone([(587.33, 0.055), (880.0, 0.075)]);
    private readonly SoundPlayer _stop = Tone([(880.0, 0.05), (587.33, 0.075)]);
    private readonly SoundPlayer _error = Tone([(233.08, 0.09), (0, 0.03), (207.65, 0.13)]);

    public bool Enabled { get; set; } = true;

    public void Start() => Play(_start);
    public void Stop() => Play(_stop);
    public void Error() => Play(_error);

    private void Play(SoundPlayer player)
    {
        if (!Enabled) return;
        try
        {
            player.Stream!.Position = 0;
            player.Play();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo reproducir el sonido", ex);
        }
    }

    private static SoundPlayer Tone((double Hz, double Seconds)[] notes)
    {
        const int rate = 44100;
        const double volume = 0.16;
        var samples = new List<short>();
        foreach (var (hz, seconds) in notes)
        {
            var n = (int)(rate * seconds);
            for (var i = 0; i < n; i++)
            {
                // Short fade in and out so the note does not click.
                var edge = Math.Min(1.0, Math.Min(i, n - 1 - i) / (rate * 0.008));
                var value = hz <= 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / rate) * volume * edge;
                samples.Add((short)(value * short.MaxValue));
            }
        }

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        return player;
    }
}
