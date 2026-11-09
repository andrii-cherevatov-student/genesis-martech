using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MarTechSolution.Services;

public sealed class GeminiOptions
{
    public string Model { get; set; } = "gemini-3.6-flash";
    /// <summary>"interactions" (recommended) or "generateContent" (legacy, still supported).</summary>
    public string Api { get; set; } = "interactions";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
    /// <summary>Tried in order when the primary model stays overloaded (503/429).</summary>
    public string[] FallbackModels { get; set; } = [];
    /// <summary>Max seconds to wait for one analysis call before giving up on it (then the next fallback model is tried).</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;
    /// <summary>Optional: "low" / "medium" / "high". Lower is faster and cheaper but reads small on-screen text less reliably.</summary>
    public string? MediaResolution { get; set; }
    /// <summary>Fallback when the GEMINI_API_KEY environment variable is not set. Keep it out of source control.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Gemini video understanding: Files API upload → wait until ACTIVE → structured JSON analysis.</summary>
public sealed class GeminiClient(HttpClient http, IOptionsMonitor<GeminiOptions> options, IConfiguration config, ILogger<GeminiClient> log)
{
    GeminiOptions _o => options.CurrentValue; // re-read so config edits apply without a restart
    string Base => _o.BaseUrl.TrimEnd('/');

    string? Key => config["GEMINI_API_KEY"] is { Length: > 0 } k ? k : _o.ApiKey;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);
    public string Model => _o.Model;

    string RequireKey() => Key is { Length: > 0 } k ? k
        : throw new AppException(400, "Gemini API key is not configured.",
            "Set GEMINI_API_KEY (or Gemini:ApiKey in appsettings.json), or click “Load Demo” to explore with sample results.");

    /// <summary>Uploads a video via the Files API, analyzes it, then deletes the uploaded file.</summary>
    public async Task<(T Result, string Raw, string Api, string Model)> AnalyzeVideoAsync<T>(Stream video, long length, string mimeType, string fileName,
        string prompt, JsonNode schema, CancellationToken ct)
    {
        var key = RequireKey();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        log.LogInformation("Gemini: uploading {File} ({MB:0.0} MB)…", fileName, length / 1048576.0);
        var file = await UploadAsync(video, length, mimeType, fileName, key, ct);
        log.LogInformation("Gemini: uploaded in {S:0.0}s, waiting for processing…", sw.Elapsed.TotalSeconds);
        try
        {
            var uri = await WaitUntilActiveAsync(file, key, ct);
            log.LogInformation("Gemini: file ready after {S:0.0}s.", sw.Elapsed.TotalSeconds);
            return await AnalyzeUriAsync<T>(uri, mimeType, prompt, schema, key, ct);
        }
        finally
        {
            // Best-effort cleanup; files also expire automatically after 48h.
            try { await SendAsync(HttpMethod.Delete, $"{Base}/v1beta/{file}", key, null, CancellationToken.None); }
            catch (Exception ex) { log.LogDebug(ex, "Could not delete Gemini file {File}", file); }
        }
    }

    /// <summary>Gemini reads public YouTube URLs directly, so no upload is needed.</summary>
    public Task<(T Result, string Raw, string Api, string Model)> AnalyzeYouTubeAsync<T>(string url, string prompt, JsonNode schema, CancellationToken ct) =>
        AnalyzeUriAsync<T>(url, "video/mp4", prompt, schema, RequireKey(), ct);

    async Task<(T Result, string Raw, string Api, string Model)> AnalyzeUriAsync<T>(string uri, string mimeType, string prompt, JsonNode schema, string key, CancellationToken ct)
    {
        var models = new[] { _o.Model }.Concat(_o.FallbackModels ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();
        for (var i = 0; ; i++)
        {
            try { return await AnalyzeWithModelAsync<T>(models[i], uri, mimeType, prompt, schema, key, ct); }
            catch (AppException ex) when (i < models.Length - 1 && IsTransient(ex.Message))
            {
                log.LogWarning("Gemini model {Model} unavailable, falling back to {Next}.", models[i], models[i + 1]);
            }
        }
    }

    async Task<(T Result, string Raw, string Api, string Model)> AnalyzeWithModelAsync<T>(string model, string uri, string mimeType, string prompt, JsonNode schema, string key, CancellationToken ct)
    {
        var useLegacy = _o.Api.Equals("generateContent", StringComparison.OrdinalIgnoreCase);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                string text, api = useLegacy ? "generateContent" : "interactions";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                log.LogInformation("Gemini: analyzing with {Model} via {Api} (attempt {Attempt})…", model, api, attempt);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _o.RequestTimeoutSeconds)));
                try
                {
                    try
                    {
                        text = useLegacy
                            ? await GenerateContentAsync(model, uri, mimeType, prompt, schema, key, timeout.Token)
                            : await InteractionsAsync(model, uri, mimeType, prompt, schema, key, timeout.Token);
                    }
                    catch (AppException ex) when (!useLegacy && ex.Message.Contains("(404)"))
                    {
                        log.LogWarning("Interactions endpoint unavailable, falling back to generateContent.");
                        text = await GenerateContentAsync(model, uri, mimeType, prompt, schema, key, timeout.Token);
                        api = "generateContent";
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Treated as "busy" so the next fallback model gets a chance.
                    throw new AppException(504, $"Gemini error (504): {model} didn't answer within {_o.RequestTimeoutSeconds}s.",
                        "Long videos take longer. Try a shorter cut of the ad, or set Gemini:MediaResolution to \"low\".");
                }
                log.LogInformation("Gemini: {Model} answered in {S:0.0}s.", model, sw.Elapsed.TotalSeconds);
                var json = StripFences(text);
                return (Upstream.ParseJson<T>(json, "Gemini"), json, api, model);
            }
            // "High demand" (503) and rate limits are usually short-lived: back off and retry (timeouts move on to the fallback).
            catch (AppException ex) when (attempt < 3 && IsTransient(ex.Message) && !ex.Message.Contains("(504)"))
            {
                var delay = TimeSpan.FromSeconds(5 * attempt * attempt);
                log.LogWarning("Gemini {Model} busy (attempt {Attempt}), retrying in {Delay}s: {Message}", model, attempt, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, ct);
            }
        }
    }

    JsonObject VideoPart(string uri, string mimeType)
    {
        var part = new JsonObject { ["type"] = "video", ["uri"] = uri, ["mime_type"] = mimeType };
        if (!string.IsNullOrWhiteSpace(_o.MediaResolution)) part["resolution"] = _o.MediaResolution;
        return part;
    }

    static bool IsTransient(string message) =>
        message.Contains("(503)") || message.Contains("(429)") || message.Contains("(500)") || message.Contains("(502)") || message.Contains("(504)");

    async Task<string> UploadAsync(Stream video, long length, string mimeType, string fileName, string key, CancellationToken ct)
    {
        using var start = new HttpRequestMessage(HttpMethod.Post, $"{Base}/upload/v1beta/files")
        {
            Content = new StringContent(new JsonObject { ["file"] = new JsonObject { ["display_name"] = fileName } }.ToJsonString(),
                Encoding.UTF8, "application/json")
        };
        start.Headers.Add("x-goog-api-key", key);
        start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        start.Headers.Add("X-Goog-Upload-Command", "start");
        start.Headers.Add("X-Goog-Upload-Header-Content-Length", length.ToString());
        start.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);

        using var startRes = await http.SendAsync(start, ct);
        if (!startRes.IsSuccessStatusCode)
            throw Upstream.Error("Gemini", (int)startRes.StatusCode, await startRes.Content.ReadAsStringAsync(ct), _o.Model, "Gemini__Model");
        var uploadUrl = startRes.Headers.TryGetValues("x-goog-upload-url", out var v) ? v.First()
            : throw new AppException(502, "Gemini did not return an upload URL.");

        using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = new StreamContent(video) };
        upload.Content.Headers.ContentLength = length;
        upload.Headers.Add("X-Goog-Upload-Offset", "0");
        upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");

        using var upRes = await http.SendAsync(upload, ct);
        var raw = await upRes.Content.ReadAsStringAsync(ct);
        if (!upRes.IsSuccessStatusCode) throw Upstream.Error("Gemini", (int)upRes.StatusCode, raw, _o.Model, "Gemini__Model");
        return JsonNode.Parse(raw)?["file"]?["name"]?.GetValue<string>()
               ?? throw new AppException(502, "Gemini upload succeeded but returned no file name.");
    }

    async Task<string> WaitUntilActiveAsync(string fileName, string key, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            var node = JsonNode.Parse(await SendAsync(HttpMethod.Get, $"{Base}/v1beta/{fileName}", key, null, ct))!;
            var state = node["state"]?.GetValue<string>();
            if (state == "ACTIVE") return node["uri"]!.GetValue<string>();
            if (state == "FAILED")
                throw new AppException(422, "Gemini could not process this video.", "Make sure it's a valid MP4 (H.264/AAC) and try re-exporting it.");
            if (DateTime.UtcNow > deadline)
                throw new AppException(504, "Gemini took too long to process the video.", "Try a shorter or smaller video file.");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    async Task<string> InteractionsAsync(string model, string uri, string mimeType, string prompt, JsonNode schema, string key, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(
                VideoPart(uri, mimeType),
                new JsonObject { ["type"] = "text", ["text"] = prompt }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "text",
                ["mime_type"] = "application/json",
                ["schema"] = schema.DeepClone()
            }
        };
        var node = JsonNode.Parse(await SendAsync(HttpMethod.Post, $"{Base}/v1beta/interactions", key, body, ct))!;

        var status = node["status"]?.GetValue<string>();
        if (status is "failed" or "cancelled" or "incomplete")
            throw new AppException(502, $"Gemini interaction {status}: {node["errors"]?[0]?["message"]}", "Try again, or set Gemini:Api to generateContent.");

        if (node["output_text"]?.GetValue<string>() is { Length: > 0 } direct) return direct;
        var sb = new StringBuilder();
        foreach (var step in node["steps"]?.AsArray() ?? [])
        {
            if (step?["type"]?.GetValue<string>() != "model_output") continue;
            foreach (var part in step["content"]?.AsArray() ?? [])
                if (part?["type"]?.GetValue<string>() == "text") sb.Append(part["text"]?.GetValue<string>());
        }
        return sb.ToString();
    }

    async Task<string> GenerateContentAsync(string model, string uri, string mimeType, string prompt, JsonNode schema, string key, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["file_data"] = new JsonObject { ["mime_type"] = mimeType, ["file_uri"] = uri } },
                    new JsonObject { ["text"] = prompt })
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseJsonSchema"] = schema.DeepClone()
            }
        };
        var node = JsonNode.Parse(await SendAsync(HttpMethod.Post, $"{Base}/v1beta/models/{model}:generateContent", key, body, ct))!;
        var sb = new StringBuilder();
        foreach (var part in node["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? [])
            if (part?["text"] is { } t) sb.Append(t.GetValue<string>());
        if (sb.Length == 0)
            throw new AppException(502, $"Gemini returned no content (finish reason: {node["candidates"]?[0]?["finishReason"] ?? node["promptFeedback"]?["blockReason"]}).");
        return sb.ToString();
    }

    async Task<string> SendAsync(HttpMethod method, string url, string key, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("x-goog-api-key", key);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode && !(method == HttpMethod.Delete && res.StatusCode == HttpStatusCode.NotFound))
            throw Upstream.Error("Gemini", (int)res.StatusCode, raw, _o.Model, "Gemini__Model");
        return raw;
    }

    static string StripFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var first = s.IndexOf('\n');
        var last = s.LastIndexOf("```", StringComparison.Ordinal);
        return first > 0 && last > first ? s[(first + 1)..last] : s;
    }
}
