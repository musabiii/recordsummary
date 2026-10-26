using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RecordSummary;

/// <summary>
/// OpenAI-совместимый /chat/completions: OpenRouter, а также локальные Ollama, LM Studio, llama-server —
/// у всех один формат, отличается только адрес.
/// </summary>
public sealed class ApiLlm : ILlm
{
    readonly HttpClient _http;
    readonly Config _cfg;

    public string Name => $"{_cfg.ApiModel} ({new Uri(_cfg.ApiBaseUrl).Host})";
    public int MaxInputChars => _cfg.ApiMaxInputChars;

    public ApiLlm(Config cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { BaseAddress = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        var key = string.IsNullOrWhiteSpace(cfg.ApiKey) ? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") : cfg.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else if (_http.BaseAddress.Host.Contains("openrouter"))
            throw new InvalidOperationException("Нет ключа OpenRouter: укажите ApiKey в config.json или переменную OPENROUTER_API_KEY");
        // Необязательные заголовки OpenRouter: название приложения в их статистике.
        _http.DefaultRequestHeaders.Add("X-Title", "RecordSummary");
    }

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["model"] = _cfg.ApiModel,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.3,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        };
        using var response = await _http.PostAsJsonAsync("chat/completions", request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{_cfg.ApiBaseUrl}: {(int)response.StatusCode} {Truncate(body)}");

        var json = JsonNode.Parse(body);
        // OpenRouter может вернуть 200 с ошибкой провайдера внутри.
        if (json?["error"] is JsonNode error) throw new HttpRequestException($"{_cfg.ApiModel}: {error["message"] ?? error}");
        var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new HttpRequestException($"Пустой ответ: {Truncate(body)}");
        var usage = json?["usage"];
        Log.Write($"API {_cfg.ApiModel}: {usage?["prompt_tokens"]} → {usage?["completion_tokens"]} токенов");
        return content;
    }

    /// <summary>Модели сервера (/models): у OpenRouter — весь каталог, у Ollama и LM Studio — скачанные.</summary>
    public async Task<List<string>> ModelsAsync(CancellationToken ct = default)
    {
        var json = JsonNode.Parse(await _http.GetStringAsync("models", ct));
        return json?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Order().ToList() ?? [];
    }

    static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;

    public void Dispose() => _http.Dispose();
}
