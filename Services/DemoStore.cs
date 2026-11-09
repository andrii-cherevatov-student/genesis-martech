using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MarTechSolution.Services;

/// <summary>
/// Sample results for demo mode (no API keys needed). The sample data goes through the same C# scorer and
/// compliance checks as live results, so the demo shows real scoring behaviour.
/// </summary>
public sealed class DemoStore(IWebHostEnvironment env, IOptions<MatchingOptions> matching)
{
    record DemoFile(
        string WinnerScript, CreativeAnalysis Competitor, CreativeAnalysis Winner,
        List<MatchCandidate> MatchCandidates, List<Variation> ModifierVariations, List<Variation> CreatorVariations);

    object? _cached;

    public object Get()
    {
        if (_cached is not null) return _cached;
        var path = Path.Combine(env.ContentRootPath, "Data", "demo.json");
        var d = JsonSerializer.Deserialize<DemoFile>(File.ReadAllText(path), Upstream.Json)!;
        var competitor = Guardrails.Normalize(d.Competitor);
        var winner = Guardrails.Normalize(d.Winner);
        var modifier = d.ModifierVariations.Select(v => Guardrails.Finalize(v, competitor)).ToList();
        Guardrails.FlagSimilarHooks(modifier);

        return _cached = new
        {
            d.WinnerScript,
            competitor,
            winner,
            match = new MatchResponse(MatchScorer.ScoreAll(d.MatchCandidates, competitor, winner, matching.Value), MatchScorer.Formula),
            variations = new
            {
                modifier,
                creator = d.CreatorVariations.Select(v => Guardrails.Finalize(v, competitor)).ToList()
            }
        };
    }
}
