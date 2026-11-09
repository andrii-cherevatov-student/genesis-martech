using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MarTechSolution.Services;

public sealed class OpenAiOptions
{
    public string Model { get; set; } = "gpt-6-luna";
    /// <summary>Optional stronger model for creative generation only; falls back to Model.</summary>
    public string? GenerationModel { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    /// <summary>Reasoning effort for analysis and matching ("low" is much faster than the model default).</summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>Reasoning effort for creative generation; falls back to ReasoningEffort.</summary>
    public string? GenerationReasoningEffort { get; set; }
    public int MaxOutputTokens { get; set; } = 24000;
    /// <summary>Fallback when the OPENAI_API_KEY environment variable is not set. Keep it out of source control.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Minimal client for the OpenAI Responses API with strict JSON-schema structured output.</summary>
public sealed class OpenAiClient(HttpClient http, IOptionsMonitor<OpenAiOptions> options, IConfiguration config, ILogger<OpenAiClient> log)
{
    OpenAiOptions _o => options.CurrentValue; // re-read so config edits apply without a restart

    string? Key => config["OPENAI_API_KEY"] is { Length: > 0 } k ? k : _o.ApiKey;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);
    public string Model => _o.Model;
    public string GenerationModel => string.IsNullOrWhiteSpace(_o.GenerationModel) ? _o.Model : _o.GenerationModel;

    public async Task<T> GenerateJsonAsync<T>(string schemaName, JsonNode schema, string instructions, string input, CancellationToken ct, bool generation = false)
    {
        var model = generation ? GenerationModel : _o.Model;
        var effort = generation && !string.IsNullOrWhiteSpace(_o.GenerationReasoningEffort) ? _o.GenerationReasoningEffort : _o.ReasoningEffort;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var key = Key;
        if (string.IsNullOrWhiteSpace(key))
            throw new AppException(400, "OpenAI API key is not configured.",
                "Set the OPENAI_API_KEY environment variable and restart the app — or click “Load Demo” to explore with sample results.");

        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = instructions,
            ["input"] = input,
            ["store"] = false,
            ["max_output_tokens"] = _o.MaxOutputTokens,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = schemaName,
                    ["strict"] = true,
                    ["schema"] = schema.DeepClone()
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(effort))
            body["reasoning"] = new JsonObject { ["effort"] = effort };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_o.BaseUrl.TrimEnd('/')}/responses")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var res = await http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw Upstream.Error("OpenAI", (int)res.StatusCode, raw, model, "OpenAI__Model / OpenAI__GenerationModel");

        var doc = JsonNode.Parse(raw)!;
        log.LogInformation("OpenAI {Schema}: {Model} effort={Effort} {Seconds:0.0}s, output {Out} tokens ({Reasoning} reasoning)",
            schemaName, model, effort ?? "default", sw.Elapsed.TotalSeconds,
            doc["usage"]?["output_tokens"], doc["usage"]?["output_tokens_details"]?["reasoning_tokens"]);
        if (doc["status"]?.GetValue<string>() == "incomplete")
            throw new AppException(502, $"OpenAI response was cut off ({doc["incomplete_details"]?["reason"]}).",
                "Increase OpenAI__MaxOutputTokens or request fewer variations.");

        var text = new System.Text.StringBuilder();
        foreach (var item in doc["output"]?.AsArray() ?? [])
        {
            if (item?["type"]?.GetValue<string>() != "message") continue;
            foreach (var part in item["content"]?.AsArray() ?? [])
            {
                var type = part?["type"]?.GetValue<string>();
                if (type == "refusal")
                    throw new AppException(422, "The model declined this request.", part!["refusal"]?.GetValue<string>());
                if (type == "output_text") text.Append(part!["text"]?.GetValue<string>());
            }
        }
        return Upstream.ParseJson<T>(text.ToString(), "OpenAI");
    }
}

static class Upstream
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static T ParseJson<T>(string text, string provider)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new AppException(502, $"{provider} returned an empty response.", "Try again; if it persists, check the model name.");
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw new JsonException("null");
        }
        catch (JsonException ex)
        {
            throw new AppException(502, $"{provider} returned JSON that doesn't match the expected structure.", ex.Message);
        }
    }

    public static AppException Error(string provider, int status, string raw, string model, string modelSetting)
    {
        string? message = null;
        try
        {
            var n = JsonNode.Parse(raw);
            message = n?["error"]?["message"]?.GetValue<string>() ?? n?["errors"]?[0]?["message"]?.GetValue<string>();
        }
        catch { /* non-JSON error body */ }
        message ??= raw.Length > 300 ? raw[..300] : raw;

        var hint = status switch
        {
            401 or 403 => $"Check that your {provider} API key is valid and has access to this API.",
            404 => $"Model “{model}” or endpoint not found. Set {modelSetting} to a model your account can use.",
            429 when message.Contains("credit", StringComparison.OrdinalIgnoreCase) || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                => $"Billing issue: the {provider} account or project behind this API key has no usable balance or has hit its spend limit. Changing models won't help. Check billing and limits, or click “Load Demo” meanwhile.",
            429 => $"{provider} rate limit hit. Wait a moment and try again.",
            >= 500 => $"{provider} is having trouble. Try again in a minute.",
            _ => null
        };
        return new AppException(502, $"{provider} error ({status}): {message}", hint);
    }
}
