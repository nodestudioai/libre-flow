using System.Windows.Threading;
using LibreFlow.Audio;
using LibreFlow.Core.Storage;
using LibreFlow.Input;
using LibreFlow.Services;
using LibreFlow.UI;

namespace LibreFlow.Speech;

/// <summary>Owns one dictation from key press to pasted text. Everything here runs on the UI thread.</summary>
internal sealed class DictationController
{
    private enum Phase { Idle, Recording, Processing }

    private static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(10);

    private readonly SettingsStore _settings;
    private readonly Database _db;
    private readonly InputController _input;
    private readonly Recorder _recorder = new();
    private readonly Sounds _sounds = new();
    private readonly SystemAudio _systemAudio = new();
    private readonly DictationPipeline _pipeline;
    private readonly PasteService _paste;
    private readonly FlowBarWindow _bar;
    private readonly DispatcherTimer _limitTimer;

    private Phase _phase = Phase.Idle;
    private string _mode = "ptt";
    private CancellationTokenSource? _cts;

    public DictationController(SettingsStore settings, Database db, InputController input, DictationPipeline pipeline,
        PasteService paste, FlowBarWindow bar)
    {
        _settings = settings;
        _db = db;
        _input = input;
        _pipeline = pipeline;
        _paste = paste;
        _bar = bar;
        _limitTimer = new DispatcherTimer { Interval = MaxRecording };
        _limitTimer.Tick += (_, _) => _ = FinishAsync();
        _recorder.Level += level => _bar.PushLevel(level);
    }

    /// <summary>Raised after a dictation is saved, so an open dashboard can refresh.</summary>
    public event Action? HistoryChanged;

    /// <summary>Raised when the user must add an API key before dictating.</summary>
    public event Action? ApiKeyNeeded;

    public bool IsBusy => _phase != Phase.Idle;

    public void Start(string mode)
    {
        if (_phase != Phase.Idle)
        {
            _input.ForceIdle();
            if (_phase == Phase.Processing) _bar.ShowMessage("Todavía estoy terminando el dictado anterior", isError: false);
            return;
        }
        if (!_settings.HasApiKey)
        {
            _input.ForceIdle();
            _bar.ShowMessage("Falta la clave de la API para transcribir", isError: true);
            ApiKeyNeeded?.Invoke();
            return;
        }

        try
        {
            _recorder.Start(_settings.Current.MicDevice);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo abrir el micrófono", ex);
            _input.ForceIdle();
            _sounds.Error();
            _bar.ShowMessage("No pude abrir el micrófono. Revisá que esté conectado y permitido.", isError: true);
            return;
        }

        _phase = Phase.Recording;
        _mode = mode;
        _input.DictationBusy = true;
        _sounds.Enabled = _settings.Current.Sounds;
        _sounds.Start();
        if (_settings.Current.MuteWhileDictating) _ = MuteAfterCueAsync();
        _bar.SetRecording(mode == "handsfree");
        _limitTimer.Stop();
        _limitTimer.Start();
    }

    private async Task MuteAfterCueAsync()
    {
        await Task.Delay(190); // let the start cue play first
        if (_phase == Phase.Recording) _systemAudio.Mute();
    }

    /// <summary>The double tap landed: keep the same recording going, now without holding keys.</summary>
    public void LockHandsFree()
    {
        if (_phase != Phase.Recording) return;
        _mode = "handsfree";
        _bar.SetRecording(handsFree: true);
    }

    /// <summary>Click on the floating bar: start hands-free, or finish the recording in progress.</summary>
    public void ToggleFromBar()
    {
        if (_phase == Phase.Recording)
        {
            _input.ForceIdle();
            _ = FinishAsync();
        }
        else if (_phase == Phase.Idle)
        {
            Start("handsfree");
            if (_phase == Phase.Recording) _input.EnterHandsFree();
        }
    }

    public void Cancel()
    {
        if (_phase == Phase.Recording)
        {
            _limitTimer.Stop();
            _recorder.Cancel();
            _systemAudio.Restore();
            _input.ForceIdle();
            EndDictation();
        }
        else if (_phase == Phase.Processing)
        {
            _cts?.Cancel();
        }
    }

    public async Task FinishAsync()
    {
        if (_phase != Phase.Recording) return;
        _phase = Phase.Processing;
        _limitTimer.Stop();
        _systemAudio.Restore();
        _sounds.Stop();
        _bar.SetProcessing();

        var mode = _mode;
        _cts = new CancellationTokenSource();
        try
        {
            var recording = await _recorder.StopAsync();
            if (recording is null) return;
            var app = ForegroundInfo.Current();
            var outcome = await Task.Run(() => _pipeline.RunAsync(recording, app, mode, _cts.Token));

            switch (outcome.Kind)
            {
                case OutcomeKind.Text:
                    _input.RequestTypingReset();
                    await _paste.PasteAsync(outcome.Text + (_settings.Current.TrailingSpace ? " " : ""));
                    if (outcome.Entry?.Status == "raw") _bar.ShowMessage("Pegué el texto sin limpiar", isError: false);
                    break;
                case OutcomeKind.Silent:
                    _bar.ShowMessage("No se escuchó nada", isError: false);
                    break;
                case OutcomeKind.Failed:
                    _sounds.Error();
                    _bar.ShowMessage(outcome.Error ?? "No se pudo transcribir", isError: true);
                    break;
            }
            if (outcome.Entry is not null) HistoryChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Esc during processing: drop the result silently.
        }
        catch (Exception ex)
        {
            Log.Error("Error al terminar el dictado", ex);
            _bar.ShowMessage("Ocurrió un error inesperado", isError: true);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            EndDictation();
        }
    }

    private void EndDictation()
    {
        _phase = Phase.Idle;
        _input.DictationBusy = false;
        _bar.SetIdle();
    }

    public async Task PasteLastAsync()
    {
        var last = _db.LastSuccessful();
        if (last is null)
        {
            _bar.ShowMessage("Todavía no hay dictados para pegar", isError: false);
            return;
        }
        _input.RequestTypingReset();
        await _paste.PasteAsync(last.Text);
    }

    public async Task<DictationOutcome> RetryAsync(long historyId)
    {
        var entry = _db.GetHistoryById(historyId);
        if (entry is null) return new DictationOutcome(OutcomeKind.Failed, Error: "Ese dictado ya no existe.");
        var outcome = await Task.Run(() => _pipeline.RetryAsync(entry, CancellationToken.None));
        HistoryChanged?.Invoke();
        return outcome;
    }

    public void Shutdown()
    {
        _recorder.Dispose();
        _systemAudio.Restore();
    }
}
