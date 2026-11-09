using System.Text;
using System.Text.RegularExpressions;

namespace MarTechSolution.Services;

/// <summary>Deterministic checks on model output: structural validation and SockLock compliance.</summary>
public static partial class Guardrails
{
    public const string SockLockFacts =
        """
        SockLock — approved facts (the ONLY product claims allowed):
        - SockLock socks have magnets on the sock cuffs that keep each pair together (e.g. click the pair together before the wash).
        - Initial offer: five pairs for $29.
        - After the first box, new pairs arrive by quarterly subscription delivery.
        Never invent other claims, statistics, testimonials, reviews, ratings, customer counts, guarantees, discounts, urgency or scarcity.
        """;

    // ---------- Structural validation ----------

    public static List<string> ValidateAnalysis(CreativeAnalysis? a, bool requireTimestamps)
    {
        var errors = new List<string>();
        if (a is null) return ["Empty analysis."];
        if (a.Segments is not { Count: > 0 }) return ["No segments returned."];

        var ids = new HashSet<string>();
        foreach (var s in a.Segments)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || !ids.Add(s.Id)) errors.Add($"Missing or duplicate segment id '{s.Id}'.");
            if (Roles.Normalize(s.Role) is null) errors.Add($"Segment {s.Id}: unknown role '{s.Role}'.");
            if (s.Observed is null || s.Interpretation is null) errors.Add($"Segment {s.Id}: missing observed/interpretation.");
            if (requireTimestamps && (s.StartSec is null || s.EndSec is null)) errors.Add($"Segment {s.Id}: missing timestamps.");
            if (s.StartSec > s.EndSec) errors.Add($"Segment {s.Id}: start is after end.");
            if (s.Interpretation is { } i && string.IsNullOrWhiteSpace(i.Evidence)) errors.Add($"Segment {s.Id}: interpretation has no evidence.");
        }
        foreach (var t in a.Techniques ?? [])
            foreach (var sid in t.SourceSegmentIds ?? [])
                if (!ids.Contains(sid)) errors.Add($"Technique {t.Id} references unknown segment '{sid}'.");
        return errors;
    }

    public static CreativeAnalysis Normalize(CreativeAnalysis a) => a with
    {
        Segments = a.Segments
            .Select(s => s with
            {
                Role = Roles.Normalize(s.Role) ?? s.Role,
                Interpretation = s.Interpretation with { Confidence = Math.Clamp(s.Interpretation.Confidence, 0, 1) }
            })
            .OrderBy(s => s.StartSec ?? double.MaxValue).ToList(),
        Techniques = a.Techniques ?? [],
        Preserve = a.Preserve ?? [],
        ProductBenefits = a.ProductBenefits ?? []
    };

    public static List<string> ValidateVariations(List<Variation>? vs)
    {
        if (vs is not { Count: > 0 }) return ["No variations returned."];
        var errors = new List<string>();
        foreach (var v in vs)
        {
            if (v.Scenes is not { Count: >= 3 }) errors.Add($"Variation '{v.Name}': needs at least 3 scenes.");
            if (string.IsNullOrWhiteSpace(v.Hook)) errors.Add($"Variation '{v.Name}': missing hook.");
        }
        return errors;
    }

    // ---------- Post-processing ----------

    public static string BuildFullScript(Variation v)
    {
        var sb = new StringBuilder();
        foreach (var s in v.Scenes.OrderBy(s => s.Number))
        {
            sb.AppendLine($"SCENE {s.Number} — {s.Role.ToUpperInvariant()} ({s.Timing})");
            if (!string.IsNullOrWhiteSpace(s.VoiceOver)) sb.AppendLine($"VO: {s.VoiceOver}");
            if (!string.IsNullOrWhiteSpace(s.OnScreenText)) sb.AppendLine($"ON-SCREEN: {s.OnScreenText}");
            if (!string.IsNullOrWhiteSpace(s.VisualDirection)) sb.AppendLine($"VISUAL: {s.VisualDirection}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static Variation Finalize(Variation v, CreativeAnalysis? competitor) =>
        v with { FullScript = BuildFullScript(v), ComplianceWarnings = CheckCompliance(v, competitor) };

    static readonly (Regex Pattern, string Message)[] Rules =
    [
        (R(@"\b\d{1,3}\s?%|\bpercent\b|\bdiscount|\bsale\b|\bcoupon|\bpromo code|\bbogo\b|\bfree (shipping|gift|pair)"), "Possible invented discount or promotion"),
        (R(@"\bguarantee|\bmoney[- ]back|\brefund|\bwarranty|\brisk[- ]free"), "Unapproved guarantee"),
        (R(@"\bclinically|\bdoctor|\bdermatolog|\bscientifically|\bproven\b|\bcertified|#1\b|\bbest[- ]?selling|\bmillions?\b|\breviews?\b|\b\d(\.\d)?\s?stars?\b|\brated\b|\btestimonial|\bcustomers (love|say)"), "Possible unsupported proof claim or testimonial"),
        (R(@"\bsell(ing)? out|\blimited (time|stock)|\bonly \d+ left|\bends (today|tonight|soon)|\blast chance"), "Invented urgency or scarcity")
    ];

    static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"\$\s?(\d+(?:\.\d{2})?)")]
    private static partial Regex PriceRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}\s']")]
    private static partial Regex Punctuation();

    public static List<string> CheckCompliance(Variation v, CreativeAnalysis? competitor)
    {
        var text = string.Join("\n", v.Scenes.SelectMany(s => new[] { s.VoiceOver, s.OnScreenText }).Append(v.Hook));
        var warnings = new List<string>();

        foreach (Match m in PriceRegex().Matches(text))
            if (m.Groups[1].Value is not ("29" or "29.00"))
                warnings.Add($"Mentions price \"{m.Value}\" — the only approved price is five pairs for $29.");

        foreach (var (pattern, message) in Rules)
            if (pattern.Match(text) is { Success: true } hit)
                warnings.Add($"{message}: \"{hit.Value.Trim()}\".");

        // Direct-copy check: any 6-word run shared with the competitor's spoken or on-screen text.
        if (competitor is not null)
        {
            var theirs = Shingles(string.Join(" ", competitor.Segments.SelectMany(s => new[] { s.Observed.SpokenText, s.Observed.OnScreenText })), 6);
            var copied = Shingles(text, 6).FirstOrDefault(theirs.Contains);
            if (copied is not null) warnings.Add($"Possible direct copy of competitor wording: \"{copied}\".");
        }
        return warnings;
    }

    /// <summary>Flags modifier variations whose hooks are near-duplicates of each other.</summary>
    public static void FlagSimilarHooks(List<Variation> vs)
    {
        for (var i = 0; i < vs.Count; i++)
            for (var j = i + 1; j < vs.Count; j++)
            {
                var a = Words(vs[i].Hook).ToHashSet();
                var b = Words(vs[j].Hook).ToHashSet();
                if (a.Count == 0 || b.Count == 0) continue;
                var jaccard = (double)a.Intersect(b).Count() / a.Union(b).Count();
                if (jaccard > 0.6) vs[j].ComplianceWarnings.Add($"Hook is very similar to \"{vs[i].Name}\" — consider a more distinct opening.");
            }
    }

    static IEnumerable<string> Words(string? s) =>
        Punctuation().Replace(s ?? "", " ").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    static HashSet<string> Shingles(string s, int n)
    {
        var w = Words(s).ToArray();
        var set = new HashSet<string>();
        for (var i = 0; i + n <= w.Length; i++) set.Add(string.Join(' ', w, i, n));
        return set;
    }
}
