using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibreFlow.Services;

public sealed class AppSettings
{
    // Speech engine (any OpenAI-compatible API).
    public string Provider { get; set; } = "groq";
    public string BaseUrl { get; set; } = EnginePresets.Groq.BaseUrl;
    public string AsrModel { get; set; } = EnginePresets.Groq.AsrModel;
    public string LlmModel { get; set; } = EnginePresets.Groq.LlmModel;
    /// <summary>API key encrypted for this Windows user (DPAPI). Never leaves this file in clear.</summary>
    public string ApiKeyProtected { get; set; } = "";

    /// <summary>auto (Spanish + English) | es | en</summary>
    public string Language { get; set; } = "auto";
    public bool Cleanup { get; set; } = true;
    public bool ToneByApp { get; set; } = true;
    /// <summary>Lay the dictation out in paragraphs, lists and (for long ones) titles.</summary>
    public bool AutoFormat { get; set; } = true;

    public string MicDevice { get; set; } = "";
    /// <summary>Recordings quieter than this (0..1) count as silence and are not sent. Lower it for a very quiet microphone.</summary>
    public double SilenceThreshold { get; set; } = 0.006;
    public bool Sounds { get; set; } = true;
    public bool MuteWhileDictating { get; set; } = true;

    /// <summary>clipboard | type</summary>
    public string PasteMethod { get; set; } = "clipboard";
    public bool RestoreClipboard { get; set; } = true;
    public bool TrailingSpace { get; set; } = true;

    public bool ShowBar { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public int TypingWpm { get; set; } = 40;
    public int KeepAudioCount { get; set; } = 20;

    public bool AutocorrectEnabled { get; set; } = true;
    public bool AutocorrectTypos { get; set; } = true;
    public bool AutocorrectAccents { get; set; } = true;
    public bool AutocorrectCapitalize { get; set; } = true;
    public bool AutocorrectOnEnter { get; set; } = true;
    public List<string> AutocorrectExcludedApps { get; set; } = [];

    public bool OnboardingDone { get; set; }
}

public sealed record EnginePreset(string Id, string Name, string BaseUrl, string AsrModel, string LlmModel);

public static class EnginePresets
{
    public static readonly EnginePreset Groq = new("groq", "Groq", "https://api.groq.com/openai/v1", "whisper-large-v3", "openai/gpt-oss-120b");
    public static readonly EnginePreset OpenAi = new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-transcribe", "gpt-4o-mini");

    public static EnginePreset? Find(string id) => id switch
    {
        "groq" => Groq,
        "openai" => OpenAi,
        _ => null,
    };
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();

    public AppSettings Current { get; private set; } = new();

    public bool IsFirstRun { get; private set; }

    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), Json) ?? new AppSettings();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron leer los ajustes; se usan los valores por defecto", ex);
        }
        IsFirstRun = true;
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            Directory.CreateDirectory(AppPaths.DataDir);
            var temp = AppPaths.Settings + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
            File.Move(temp, AppPaths.Settings, overwrite: true);
        }
        Changed?.Invoke();
    }

    public bool HasApiKey => Current.ApiKeyProtected.Length > 0;

    public string? GetApiKey()
    {
        if (!HasApiKey) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(Current.ApiKeyProtected), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo descifrar la clave guardada", ex);
            return null;
        }
    }

    public void SetApiKey(string key)
    {
        key = key.Trim();
        var value = key.Length == 0
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        Update(s => s.ApiKeyProtected = value);
    }
}
