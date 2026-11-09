namespace MarTechSolution.Services;

// One semantic model shared by competitor videos and our winning scripts.
// "Observed" = literally seen/heard/written. "Interpretation" = AI inference, always with evidence.

public static class Roles
{
    public static readonly string[] All = ["Hook", "Pain", "Solution", "Demonstration", "Proof", "Offer", "CTA"];

    public static string? Normalize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var r = role.Trim();
        if (r.StartsWith("demo", StringComparison.OrdinalIgnoreCase)) return "Demonstration";
        if (r.Equals("call to action", StringComparison.OrdinalIgnoreCase)) return "CTA";
        return All.FirstOrDefault(x => x.Equals(r, StringComparison.OrdinalIgnoreCase));
    }
}

public record SegmentObserved(string SpokenText, string OnScreenText, string VisualDescription, string AudioNotes);

public record SegmentInterpretation(
    string Mechanism, string Angle, string EmotionalTrigger, string StorytellingPattern,
    string ReusablePattern, string Evidence, double Confidence);

public record Segment(
    string Id, string Role, double? StartSec, double? EndSec,
    SegmentObserved Observed, SegmentInterpretation Interpretation);

public record Technique(
    string Id, string Name, string Category, string Description, List<string> SourceSegmentIds,
    double? StartSec, double? EndSec, string Evidence, string HowToReuse);

public record PreserveItem(string Element, string SegmentId, string Reason);

public record CreativeAnalysis(
    string Title, string Summary, string ProductCategory, double? DurationSec,
    string PrimaryAngle, string StorytellingPattern, string TargetAudience,
    List<Segment> Segments, List<Technique> Techniques,
    List<PreserveItem> Preserve, List<string> ProductBenefits);

// ---------- Matching ----------

public record CriterionScore(double Score, string Reason);

public record MatchCriteria(
    CriterionScore Role, CriterionScore Pain, CriterionScore Mechanism,
    CriterionScore ProductFit, CriterionScore Novelty);

public record MatchCandidate(
    string Id, string CompetitorTechniqueId, string CompetitorSegmentId, string WinnerSegmentId,
    string TechniqueName, MatchCriteria Criteria,
    string WhyTransferable, string WhatToChange, string WhatToKeep, string Hypothesis,
    bool Compatible, bool UnsupportedClaimRisk, bool DirectCopyRisk, string RiskNotes, string ExpectedImpact = "");

public record ScoredMatch(
    MatchCandidate Candidate, int Score, Dictionary<string, double> Contributions,
    bool Accepted, List<string> RejectionReasons);

public record MatchRequest(CreativeAnalysis Competitor, CreativeAnalysis Winner);
public record MatchResponse(List<ScoredMatch> Matches, string Formula);

// ---------- Generation ----------

public record Scene(int Number, string Role, string Timing, string VoiceOver, string OnScreenText, string VisualDirection);

public record InspiredBy(string MatchId, string TechniqueName, string CompetitorTimestamp, string HowApplied);

public record Variation(
    string Id, string Name, string Angle, string Hook,
    List<Scene> Scenes, List<InspiredBy> InspiredBy, int EstimatedDurationSec)
{
    // Filled by C# after generation (not part of the LLM schema).
    public string FullScript { get; init; } = "";
    public List<string> ComplianceWarnings { get; init; } = [];
}

public record GenerateRequest(
    string Mode, int Count, string WinnerScript, CreativeAnalysis Winner,
    CreativeAnalysis Competitor, List<MatchCandidate> SelectedMatches);

/// <param name="Failed">Variations whose parallel request failed (the rest are still returned).</param>
public record GenerateResponse(string Mode, List<Variation> Variations, int Failed = 0);

public record ScriptRequest(string Script);
public record VideoUrlRequest(string Url, bool Force = false);

/// <summary>Video analysis plus the raw model output, so users can see exactly how Gemini parsed the video.</summary>
public record VideoAnalysisResult(CreativeAnalysis Analysis, string Raw, string Model, string Api, double Seconds)
{
    /// <summary>"sha256:…" of the video bytes, or "youtube:{id}".</summary>
    public string? VideoKey { get; init; }
    /// <summary>True when served from the PostgreSQL cache instead of a new Gemini call.</summary>
    public bool Cached { get; init; }
    public DateTime? AnalyzedAt { get; init; }
}

public sealed class AppException(int status, string message, string? hint = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Hint { get; } = hint;
}
