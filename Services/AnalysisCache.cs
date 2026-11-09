using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace MarTechSolution.Services;

public record CachedVideoAnalysis(VideoAnalysisResult Result, DateTime AnalyzedAt);

/// <summary>
/// PostgreSQL cache of video analyses keyed by content hash ("sha256:…") or YouTube id ("youtube:…"),
/// so the same competitor ad is never sent to Gemini twice. Optional: without a connection string the app runs uncached.
/// </summary>
public sealed class AnalysisCache(IConfiguration config, ILogger<AnalysisCache> log) : IAsyncDisposable
{
    NpgsqlDataSource? _db;
    public bool Enabled => _db is not null;
    public string? Error { get; private set; }

    public async Task InitAsync(CancellationToken ct)
    {
        var cs = config.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(cs)) { Error = "No ConnectionStrings:Postgres configured."; return; }

        var db = NpgsqlDataSource.Create(cs);
        // Postgres may still be starting (e.g. under docker compose), so retry briefly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = db.CreateCommand("""
                    CREATE TABLE IF NOT EXISTS video_analyses (
                        video_key   text PRIMARY KEY,
                        source      text NOT NULL,
                        file_name   text,
                        size_bytes  bigint,
                        model       text NOT NULL,
                        api         text NOT NULL,
                        analysis    jsonb NOT NULL,
                        raw_output  text NOT NULL,
                        seconds     double precision NOT NULL,
                        analyzed_at timestamptz NOT NULL DEFAULT now()
                    )
                    """);
                await cmd.ExecuteNonQueryAsync(ct);
                _db = db; Error = null;
                log.LogInformation("Video analysis cache: connected to PostgreSQL.");
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                log.LogWarning("Video analysis cache disabled: PostgreSQL unavailable ({Message}).", ex.Message);
                await db.DisposeAsync();
                return;
            }
        }
    }

    public async Task<CachedVideoAnalysis?> GetAsync(string key, CancellationToken ct)
    {
        if (_db is null) return null;
        try
        {
            await using var cmd = _db.CreateCommand(
                "SELECT analysis::text, raw_output, model, api, seconds, analyzed_at FROM video_analyses WHERE video_key = $1");
            cmd.Parameters.AddWithValue(key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var analysis = JsonSerializer.Deserialize<CreativeAnalysis>(r.GetString(0), Upstream.Json)!;
            var result = new VideoAnalysisResult(analysis, r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4))
                { VideoKey = key, Cached = true, AnalyzedAt = r.GetDateTime(5) };
            return new CachedVideoAnalysis(result, r.GetDateTime(5));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Cache read failed for {Key}; analyzing instead.", key);
            return null;
        }
    }

    public async Task SaveAsync(string key, string source, string? fileName, long? size, VideoAnalysisResult r, CancellationToken ct)
    {
        if (_db is null) return;
        try
        {
            await using var cmd = _db.CreateCommand("""
                INSERT INTO video_analyses (video_key, source, file_name, size_bytes, model, api, analysis, raw_output, seconds)
                VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9)
                ON CONFLICT (video_key) DO UPDATE SET
                    source = EXCLUDED.source, file_name = EXCLUDED.file_name, size_bytes = EXCLUDED.size_bytes,
                    model = EXCLUDED.model, api = EXCLUDED.api, analysis = EXCLUDED.analysis,
                    raw_output = EXCLUDED.raw_output, seconds = EXCLUDED.seconds, analyzed_at = now()
                """);
            cmd.Parameters.AddWithValue(key);
            cmd.Parameters.AddWithValue(source);
            cmd.Parameters.AddWithValue((object?)fileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)size ?? DBNull.Value);
            cmd.Parameters.AddWithValue(r.Model);
            cmd.Parameters.AddWithValue(r.Api);
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(r.Analysis, Upstream.Json));
            cmd.Parameters.AddWithValue(r.Raw);
            cmd.Parameters.AddWithValue(r.Seconds);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not cache analysis for {Key}.", key);
        }
    }

    public static async Task<string> HashAsync(Stream s, CancellationToken ct) =>
        "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
    }
}
