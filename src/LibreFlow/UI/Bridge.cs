using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreFlow.Audio;
using LibreFlow.Core.Storage;
using LibreFlow.Services;
using LibreFlow.Speech;

namespace LibreFlow.UI;

/// <summary>Everything the HTML panel can ask the app to do. Runs on the UI thread.</summary>
internal sealed class Bridge
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SettingsStore _settings;
    private readonly Database _db;
    private readonly SpeechClient _client;
    private readonly DictationController _dictation;
    private readonly PasteService _paste;

    public Bridge(SettingsStore settings, Database db, SpeechClient client, DictationController dictation, PasteService paste)
    {
        _settings = settings;
        _db = db;
        _client = client;
        _dictation = dictation;
        _paste = paste;
    }

    /// <summary>Dictionary or learned words changed: the autocorrector must reload them.</summary>
    public event Action? WordsChanged;

    public Func<bool> LexiconReady { get; set; } = () => false;

    public Action Quit { get; set; } = () => { };

    public async Task<object?> HandleAsync(string method, JsonElement p)
    {
        switch (method)
        {
            case "state.get":
                return State();

            case "settings.set":
                ApplyPatch(p.GetProperty("patch"));
                return State();

            case "settings.setApiKey":
                _settings.SetApiKey(Str(p, "key"));
                return State();

            case "autostart.set":
                var startWithWindows = p.GetProperty("enabled").GetBoolean();
                _settings.Update(s => s.StartWithWindows = startWithWindows);
                return State();

            case "engine.test":
            {
                var models = await _client.ListModelsAsync(CancellationToken.None);
                return new { models };
            }

            case "insights.get":
            {
                var now = DateTime.Now;
                var insights = InsightsCalculator.Compute(_db.GetDictationRows(), now, _settings.Current.TypingWpm, days: 182);
                return new { insights, corrections = _db.GetCorrectionStats(now), typingWpm = _settings.Current.TypingWpm };
            }

            case "history.list":
            {
                var query = p.TryGetProperty("q", out var q) ? q.GetString() : null;
                var offset = p.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
                var limit = p.TryGetProperty("limit", out var l) ? Math.Clamp(l.GetInt32(), 1, 200) : 50;
                return new { items = _db.GetHistory(limit, offset, query), total = _db.CountHistory(query) };
            }

            case "history.delete":
            {
                var id = p.GetProperty("id").GetInt64();
                DeleteAudio(_db.GetHistoryById(id)?.AudioPath);
                _db.DeleteHistory(id);
                return null;
            }

            case "history.clear":
                foreach (var (_, path) in _db.AudioFiles()) DeleteAudio(path);
                _db.ClearHistory();
                return null;

            case "history.copy":
            {
                var entry = _db.GetHistoryById(p.GetProperty("id").GetInt64());
                return entry is not null && _paste.Copy(entry.Text);
            }

            case "history.retry":
            {
                var outcome = await _dictation.RetryAsync(p.GetProperty("id").GetInt64());
                if (outcome.Kind == OutcomeKind.Failed) throw new SpeechException(outcome.Error ?? "No se pudo transcribir.");
                return outcome.Entry;
            }

            case "dict.list":
                return _db.GetDictionary();

            case "dict.save":
                _db.SaveDictionaryEntry(Long(p, "id"), Str(p, "phrase"), Str(p, "replaceFrom"),
                    p.TryGetProperty("starred", out var st) && st.ValueKind == JsonValueKind.True);
                WordsChanged?.Invoke();
                return _db.GetDictionary();

            case "dict.delete":
                _db.DeleteDictionaryEntry(Long(p, "id"));
                WordsChanged?.Invoke();
                return _db.GetDictionary();

            case "snip.list":
                return _db.GetSnippets();

            case "snip.save":
                _db.SaveSnippet(Long(p, "id"), Str(p, "trigger"), Str(p, "expansion"));
                return _db.GetSnippets();

            case "snip.delete":
                _db.DeleteSnippet(Long(p, "id"));
                return _db.GetSnippets();

            case "ac.get":
                return new { recent = _db.RecentCorrections(60), learned = _db.GetLearned(), stats = _db.GetCorrectionStats(DateTime.Now) };

            case "ac.learned.add":
                _db.AddLearned(Str(p, "word"));
                WordsChanged?.Invoke();
                return _db.GetLearned();

            case "ac.learned.remove":
                _db.RemoveLearned(Str(p, "word"));
                WordsChanged?.Invoke();
                return _db.GetLearned();

            case "ac.clear":
                _db.ClearCorrections();
                return null;

            case "app.openData":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
                return null;

            case "app.quit":
                Quit();
                return null;

            default:
                throw new ArgumentException("Operación desconocida: " + method);
        }
    }

    private object State()
    {
        var s = _settings.Current;
        return new
        {
            settings = new
            {
                s.Provider, s.BaseUrl, s.AsrModel, s.LlmModel, s.Language, s.Cleanup, s.ToneByApp, s.AutoFormat, s.MicDevice, s.Sounds,
                s.MuteWhileDictating, s.PasteMethod, s.RestoreClipboard, s.TrailingSpace, s.ShowBar, s.TypingWpm,
                s.AutocorrectEnabled, s.AutocorrectTypos, s.AutocorrectAccents, s.AutocorrectCapitalize, s.AutocorrectOnEnter,
                s.AutocorrectExcludedApps, s.OnboardingDone,
            },
            hasKey = _settings.HasApiKey,
            autostart = s.StartWithWindows,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            mics = Recorder.DeviceNames(),
            presets = new[] { EnginePresets.Groq, EnginePresets.OpenAi },
            lexiconReady = LexiconReady(),
            dataDir = AppPaths.DataDir,
        };
    }

    /// <summary>Applies {"name": value} pairs to the settings, by property name. The API key has its own call.</summary>
    private void ApplyPatch(JsonElement patch)
    {
        _settings.Update(s =>
        {
            foreach (var item in patch.EnumerateObject())
            {
                var prop = typeof(AppSettings).GetProperty(item.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop is null || prop.Name == nameof(AppSettings.ApiKeyProtected)) continue;
                prop.SetValue(s, item.Value.Deserialize(prop.PropertyType, Json));
            }

            if (patch.TryGetProperty("provider", out var provider) && EnginePresets.Find(provider.GetString() ?? "") is { } preset)
            {
                s.BaseUrl = preset.BaseUrl;
                s.AsrModel = preset.AsrModel;
                s.LlmModel = preset.LlmModel;
            }
            s.TypingWpm = Math.Clamp(s.TypingWpm, 10, 200);
        });
    }

    private static void DeleteAudio(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Leave the file; it is pruned on a later dictation.
        }
    }

    private static string Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
