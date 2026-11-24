using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreFlow.Services;

namespace LibreFlow.Speech;

/// <summary>An error whose message is already written for the user, in Spanish.</summary>
internal sealed class SpeechException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed record Transcript(string Text, string? Language);

/// <summary>Client for any OpenAI-compatible API (Groq, OpenAI, others): transcription and chat cleanup.</summary>
internal sealed class SpeechClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly SettingsStore _settings;

    public SpeechClient(SettingsStore settings) => _settings = settings;

    private string BaseUrl => _settings.Current.BaseUrl.TrimEnd('/');

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var key = _settings.GetApiKey() ?? throw new SpeechException("Falta la clave de la API. Cargala en Ajustes.");
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    public async Task<Transcript> TranscribeAsync(byte[] wav, string? language, string? prompt, CancellationToken ct)
    {
        var model = _settings.Current.AsrModel;
        // Whisper can report the language it heard; other transcription models only return text.
        var verbose = model.Contains("whisper", StringComparison.OrdinalIgnoreCase);

        // The part headers are written by hand. Left to .NET, field names go out unquoted and the
        // file name is duplicated in an encoded form that some services read as part of the extension.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = "\"audio.wav\"" };
        form.Add(file);
        void Field(string name, string value)
        {
            var content = new StringContent(value);
            content.Headers.ContentType = null;
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
            form.Add(content);
        }
        Field("model", model);
        Field("response_format", verbose ? "verbose_json" : "json");
        Field("temperature", "0");
        if (!string.IsNullOrEmpty(language)) Field("language", language);
        if (!string.IsNullOrWhiteSpace(prompt)) Field("prompt", prompt);

        using var request = Request(HttpMethod.Post, "/audio/transcriptions");
        request.Content = form;
        var seconds = Audio.Recorder.WavSeconds(wav);
        var json = await SendAsync(request, TimeSpan.FromSeconds(25 + seconds / 2), ct);
        return new Transcript(json?["text"]?.GetValue<string>()?.Trim() ?? "", json?["language"]?.GetValue<string>());
    }

    public async Task<string> CleanupAsync(string system, string user, CancellationToken ct)
    {
        var model = _settings.Current.LlmModel;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["temperature"] = 0,
        };
        // Open-weight reasoning models think before answering; keep that short, this is a formatting job.
        if (model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)) body["reasoning_effort"] = "low";

        JsonNode? json;
        try
        {
            json = await PostJsonAsync("/chat/completions", body, TimeSpan.FromSeconds(8), ct);
        }
        catch (SpeechException ex) when (ex.Data["status"] is 400)
        {
            // Some models reject optional parameters (temperature, reasoning_effort). Retry with the bare minimum.
            body.Remove("temperature");
            body.Remove("reasoning_effort");
            json = await PostJsonAsync("/chat/completions", body, TimeSpan.FromSeconds(8), ct);
        }

        var text = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        return StripWrappers(text);
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/models");
        var json = await SendAsync(request, TimeSpan.FromSeconds(12), ct);
        var list = new List<string>();
        if (json?["data"] is JsonArray data)
            foreach (var item in data)
                if (item?["id"]?.GetValue<string>() is { } id) list.Add(id);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private async Task<JsonNode?> PostJsonAsync(string path, JsonObject body, TimeSpan timeout, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, path);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(request, timeout, ct);
    }

    private static async Task<JsonNode?> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using var response = await Http.SendAsync(request, timeoutCts.Token);
            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (response.IsSuccessStatusCode) return JsonNode.Parse(text);

            var detail = "";
            try
            {
                detail = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? "";
            }
            catch (JsonException)
            {
                // Not JSON: the status code alone will have to do.
            }
            var status = (int)response.StatusCode;
            var message = status switch
            {
                401 or 403 => "La clave de la API no es válida. Revisala en Ajustes.",
                404 => "El servicio no reconoce ese modelo. Elegí otro en Ajustes.",
                413 => "El audio es demasiado largo para enviarlo de una vez.",
                429 => "Se alcanzó el límite de uso del servicio. Probá de nuevo en un momento.",
                >= 500 => "El servicio de transcripción está con problemas. Probá de nuevo.",
                _ => "El servicio rechazó el pedido.",
            };
            if (detail.Length > 0) message += $" ({Truncate(detail, 160)})";
            var ex = new SpeechException(message);
            ex.Data["status"] = status;
            throw ex;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpeechException("El servicio tardó demasiado en responder.");
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechException("No hay conexión con el servicio de transcripción.", ex);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Drops reasoning blocks, echoed tags and wrapping quotes some models add around the answer.</summary>
    private static string StripWrappers(string text)
    {
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?transcript>", "", RegexOptions.IgnoreCase).Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”')))
            text = text[1..^1].Trim();
        return text;
    }
}
