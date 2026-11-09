using System.Text.Json;
using MarTechSolution.Services;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true); // git- and docker-ignored, for API keys
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

const long MaxVideoBytes = 2L * 1024 * 1024 * 1024; // Gemini Files API limit on the free tier
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxVideoBytes);
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = MaxVideoBytes);

builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.Configure<MatchingOptions>(builder.Configuration.GetSection("Matching"));
builder.Services.AddHttpClient<GeminiClient>(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<OpenAiClient>(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<VideoLinks>(c => c.Timeout = TimeSpan.FromMinutes(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<CreativeEngine>();
builder.Services.AddSingleton<DemoStore>();
builder.Services.AddSingleton<AnalysisCache>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
await app.Services.GetRequiredService<AnalysisCache>().InitAsync(app.Lifetime.ApplicationStopping);

// Turn known failures into { error, hint } JSON the UI can show as an actionable message.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (AppException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message, hint = ex.Hint });
    }
    catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested && !ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = 504;
        await ctx.Response.WriteAsJsonAsync(new { error = "The AI provider timed out.", hint = "Try again, or use a shorter video." });
    }
    catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
    {
        app.Logger.LogError(ex, "Unhandled error");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Unexpected server error.", hint = ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", (GeminiClient g, OpenAiClient o, AnalysisCache cache) => new
{
    cache = new { enabled = cache.Enabled, error = cache.Error },
    gemini = new { configured = g.IsConfigured, model = g.Model },
    openAi = new { configured = o.IsConfigured, model = o.Model, generationModel = o.GenerationModel },
    formula = MatchScorer.Formula
});

api.MapGet("/demo", (DemoStore demo) => demo.Get());

if (app.Environment.IsDevelopment())
    api.MapGet("/schemas", () => new { analysis = Schemas.Analysis, matches = Schemas.Matches, variations = Schemas.Variations });

api.MapPost("/analyze/video", async (HttpRequest req, CreativeEngine engine, CancellationToken ct) =>
{
    if (!req.HasFormContentType) throw new AppException(400, "Expected a multipart upload with a 'file' field.");
    var form = await req.ReadFormAsync(ct);
    var file = form.Files["file"] ?? form.Files.FirstOrDefault()
               ?? throw new AppException(400, "No video file received.", "Drop an MP4 file into the upload area.");
    if (file.Length == 0) throw new AppException(400, "The uploaded file is empty.");
    var mime = string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType == "application/octet-stream" ? "video/mp4" : file.ContentType;
    if (!mime.StartsWith("video/")) throw new AppException(415, $"Unsupported file type: {file.ContentType}.", "Upload an MP4 video.");

    var force = req.Query["force"] == "true";
    return await engine.AnalyzeVideoAsync(file.OpenReadStream, file.Length, mime, file.FileName, "upload", force, ct);
});

api.MapPost("/analyze/video-url", async (VideoUrlRequest r, CreativeEngine engine, CancellationToken ct) =>
    await engine.AnalyzeVideoUrlAsync(r.Url, r.Force, ct));

api.MapPost("/analyze/script", async (ScriptRequest r, CreativeEngine engine, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(r.Script) || r.Script.Trim().Length < 40)
        throw new AppException(400, "The script is too short to analyze.", "Paste a full winning script (hook through CTA), or use the sample script.");
    return await engine.AnalyzeScriptAsync(r.Script, ct);
});

api.MapPost("/match", async (MatchRequest r, CreativeEngine engine, CancellationToken ct) =>
{
    if (r.Competitor?.Segments is not { Count: > 0 } || r.Winner?.Segments is not { Count: > 0 })
        throw new AppException(400, "Both analyses are required.", "Analyze the competitor video and the winning script first.");
    return await engine.MatchAsync(r.Competitor, r.Winner, ct);
});

api.MapPost("/generate", async (GenerateRequest r, CreativeEngine engine, CancellationToken ct) =>
    await engine.GenerateAsync(r, ct));

app.Run();
