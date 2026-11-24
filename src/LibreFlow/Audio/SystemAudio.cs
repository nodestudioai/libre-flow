using NAudio.CoreAudioApi;
using LibreFlow.Services;

namespace LibreFlow.Audio;

/// <summary>Mutes the speakers while dictating so music does not leak into the microphone.</summary>
internal sealed class SystemAudio
{
    private readonly object _gate = new();
    private bool _mutedByUs;

    public void Mute()
    {
        lock (_gate)
        {
            if (_mutedByUs) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device.AudioEndpointVolume.Mute) return; // already muted by the user: leave it alone
                device.AudioEndpointVolume.Mute = true;
                _mutedByUs = true;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo silenciar el audio", ex);
            }
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (!_mutedByUs) return;
            _mutedByUs = false;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.Mute = false;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo restaurar el audio", ex);
            }
        }
    }
}
