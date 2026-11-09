namespace MarTechSolution.Services;

public sealed class MatchingOptions
{
    public int MinScore { get; set; } = 55;
    public double MinProductFit { get; set; } = 4;
}

/// <summary>
/// GPT scores each criterion 0–10 with a reason; the final score and accept/reject decision are computed here,
/// so the formula is deterministic and auditable.
/// </summary>
public static class MatchScorer
{
    public const string Formula = "Match = 0.25 × Role + 0.20 × Pain + 0.25 × Mechanism + 0.20 × Product Fit + 0.10 × Novelty";

    public static readonly (string Key, double Weight)[] Weights =
    [
        ("role", 0.25), ("pain", 0.20), ("mechanism", 0.25), ("productFit", 0.20), ("novelty", 0.10)
    ];

    static double N(CriterionScore? c) => Math.Clamp(c?.Score ?? 0, 0, 10) / 10.0;

    public static ScoredMatch Score(MatchCandidate m, CreativeAnalysis competitor, CreativeAnalysis winner, MatchingOptions opt)
    {
        var c = m.Criteria;
        var values = new Dictionary<string, double>
        {
            ["role"] = N(c.Role), ["pain"] = N(c.Pain), ["mechanism"] = N(c.Mechanism),
            ["productFit"] = N(c.ProductFit), ["novelty"] = N(c.Novelty)
        };
        // Contribution of each criterion in points (sums to the 0–100 score).
        var contributions = Weights.ToDictionary(w => w.Key, w => Math.Round(values[w.Key] * w.Weight * 100, 1));
        var score = (int)Math.Round(contributions.Values.Sum());

        var reasons = new List<string>();
        if (m.UnsupportedClaimRisk) reasons.Add("Needs a claim we can't support");
        if (m.DirectCopyRisk) reasons.Add("Copies the competitor");
        if (!m.Compatible) reasons.Add("Doesn't fit this segment");
        if (c.ProductFit.Score < opt.MinProductFit) reasons.Add($"Low product fit ({c.ProductFit.Score:0}/10)");
        if (score < opt.MinScore) reasons.Add($"Score below {opt.MinScore}");
        if (competitor.Segments.All(s => s.Id != m.CompetitorSegmentId)) reasons.Add($"Unknown competitor segment '{m.CompetitorSegmentId}'.");
        if (winner.Segments.All(s => s.Id != m.WinnerSegmentId)) reasons.Add($"Unknown winner segment '{m.WinnerSegmentId}'.");

        return new ScoredMatch(m, score, contributions, reasons.Count == 0, reasons);
    }

    public static List<ScoredMatch> ScoreAll(IEnumerable<MatchCandidate> candidates, CreativeAnalysis competitor, CreativeAnalysis winner, MatchingOptions opt) =>
        candidates.Select(m => Score(m, competitor, winner, opt))
            .OrderByDescending(s => s.Accepted).ThenByDescending(s => s.Score).ToList();
}
