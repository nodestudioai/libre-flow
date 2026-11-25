using System.Diagnostics;
using LibreFlow.Audio;
using LibreFlow.Core.Autocorrect;
using LibreFlow.Core.Speech;
using LibreFlow.Core.Storage;
using LibreFlow.Services;

namespace LibreFlow.Speech;

internal enum OutcomeKind { Text, TooShort, Silent, Failed }

internal sealed record DictationOutcome(OutcomeKind Kind, string Text = "", string? Error = null, HistoryEntry? Entry = null);

/// <summary>Audio in, final text out: transcribe, clean up with a language model, apply the user's replacements, save.</summary>
internal sealed class DictationPipeline
{
    private const double MinSeconds = 0.35;
    private const int MinWordsForCleanup = 4;

    private readonly SettingsStore _settings;
    private readonly Database _db;
    private readonly SpeechClient _client;

    public DictationPipeline(SettingsStore settings, Database db, SpeechClient client)
    {
        _settings = settings;
        _db = db;
        _client = client;
    }

    public async Task<DictationOutcome> RunAsync(Recording rec, ForegroundApp app, string mode, CancellationToken ct)
    {
        if (rec.Seconds < MinSeconds) return new DictationOutcome(OutcomeKind.TooShort);
        if (rec.PeakLevel < _settings.Current.SilenceThreshold) return new DictationOutcome(OutcomeKind.Silent);

        var entry = new HistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Duration = rec.Seconds,
            App = app.Process,
            WindowTitle = app.Title,
            Mode = mode,
            AudioPath = SaveAudio(rec.Wav),
        };
        return await ProcessAsync(entry, rec.Wav, app, isRetry: false, ct);
    }

    /// <summary>Runs a failed (or any) history entry again from its saved audio.</summary>
    public async Task<DictationOutcome> RetryAsync(HistoryEntry entry, CancellationToken ct)
    {
        if (entry.AudioPath is null || !File.Exists(entry.AudioPath))
            return new DictationOutcome(OutcomeKind.Failed, Error: "El audio de este dictado ya no está guardado.");
        var wav = await File.ReadAllBytesAsync(entry.AudioPath, ct);
        var app = new ForegroundApp(IntPtr.Zero, entry.App ?? "", entry.WindowTitle ?? "");
        return await ProcessAsync(entry, wav, app, isRetry: true, ct);
    }

    private async Task<DictationOutcome> ProcessAsync(HistoryEntry entry, byte[] wav, ForegroundApp app, bool isRetry, CancellationToken ct)
    {
        var s = _settings.Current;
        var total = Stopwatch.StartNew();
        var dictionary = _db.GetDictionary();
        try
        {
            var hint = SpeechText.VocabularyHint(dictionary.Select(d => d.Phrase));
            var prompt = hint.Length > 0 ? "Glosario: " + hint + "." : null;

            var asr = Stopwatch.StartNew();
            var transcript = await TranscribeAsync(wav, s.Language, prompt, ct);
            entry.AsrMs = (int)asr.ElapsedMilliseconds;
            entry.Language = transcript.Language;

            var raw = transcript.Text;
            if (SpeechText.IsHallucination(raw) || raw.StartsWith("Glosario:", StringComparison.OrdinalIgnoreCase))
            {
                entry.Status = "empty";
                entry.RawText = entry.Text = "";
                entry.Words = 0;
                entry.TotalMs = (int)total.ElapsedMilliseconds;
                Save(entry, isRetry);
                return new DictationOutcome(OutcomeKind.Silent, Entry: entry);
            }

            var text = SpeechText.Tidy(raw);
            entry.Status = "ok";
            entry.Error = null;
            if (s.Cleanup && TextUtil.CountWords(raw) >= MinWordsForCleanup)
            {
                var llm = Stopwatch.StartNew();
                try
                {
                    var kind = Prompts.Classify(app.Process, app.Title);
                    var cleaned = await _client.CleanupAsync(Prompts.Cleanup(kind, s.ToneByApp, hint, s.AutoFormat), Prompts.CleanupUser(raw), ct);
                    if (SpeechText.IsFaithfulCleanup(raw, cleaned)) text = SpeechText.NormalizeLayout(cleaned);
                    else
                    {
                        entry.Status = "raw";
                        entry.Error = "La limpieza cambió demasiado el texto; se usó la transcripción original.";
                    }
                }
                catch (SpeechException ex)
                {
                    // The dictation still works without the cleanup step.
                    entry.Status = "raw";
                    entry.Error = "No se pudo limpiar el texto: " + ex.Message;
                    Log.Error("Limpieza fallida: " + ex.Message);
                }
                entry.LlmMs = (int)llm.ElapsedMilliseconds;
            }

            var replacements = new List<Replacement>();
            foreach (var d in dictionary)
            {
                if (string.IsNullOrWhiteSpace(d.ReplaceFrom)) continue;
                foreach (var from in d.ReplaceFrom.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    replacements.Add(new Replacement(from, d.Phrase));
            }
            replacements.AddRange(_db.GetSnippets().Select(sn => new Replacement(sn.Trigger, sn.Expansion)));
            text = SpeechText.ApplyReplacements(text, replacements).Trim();

            entry.RawText = raw;
            entry.Text = text;
            entry.Words = TextUtil.CountWords(text);
            entry.TotalMs = (int)total.ElapsedMilliseconds;
            Save(entry, isRetry);
            PruneAudio(s.KeepAudioCount);
            return new DictationOutcome(OutcomeKind.Text, text, Entry: entry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is SpeechException ? ex.Message : "Ocurrió un error inesperado al transcribir.";
            if (ex is not SpeechException) Log.Error("Error en el dictado", ex);
            entry.Status = "failed";
            entry.Error = message;
            entry.TotalMs = (int)total.ElapsedMilliseconds;
            Save(entry, isRetry);
            return new DictationOutcome(OutcomeKind.Failed, Error: message, Entry: entry);
        }
    }

    /// <summary>
    /// In automatic mode Whisper sometimes "hears" a short Spanish phrase as Portuguese or
    /// Italian. When it reports a language the user does not speak, ask again in Spanish.
    /// </summary>
    private async Task<Transcript> TranscribeAsync(byte[] wav, string language, string? prompt, CancellationToken ct)
    {
        if (language is "es" or "en") return await _client.TranscribeAsync(wav, language, prompt, ct);

        var first = await _client.TranscribeAsync(wav, null, prompt, ct);
        var heard = first.Language?.ToLowerInvariant();
        if (heard is null or "spanish" or "english" or "es" or "en") return first;
        return await _client.TranscribeAsync(wav, "es", prompt, ct);
    }

    private void Save(HistoryEntry entry, bool isRetry)
    {
        if (isRetry || entry.Id > 0) _db.UpdateHistory(entry);
        else _db.AddHistory(entry);
    }

    private static string? SaveAudio(byte[] wav)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AudioDir);
            var path = Path.Combine(AppPaths.AudioDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
            File.WriteAllBytes(path, wav);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar el audio", ex);
            return null;
        }
    }

    /// <summary>Audio is only kept for the latest dictations, so a failed one can be retried.</summary>
    private void PruneAudio(int keep)
    {
        foreach (var (id, path) in _db.AudioFiles().Skip(Math.Max(0, keep)))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                continue; // in use: try again after the next dictation
            }
            _db.ClearAudioPath(id);
        }
    }
}
