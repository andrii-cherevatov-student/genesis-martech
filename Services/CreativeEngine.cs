using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MarTechSolution.Services;

/// <summary>The four workflow steps: video analysis, script analysis, matching, generation.</summary>
public sealed class CreativeEngine(GeminiClient gemini, OpenAiClient openAi, VideoLinks links, AnalysisCache cache,
    IOptions<MatchingOptions> matching, ILogger<CreativeEngine> log)
{
    static string J(object o) => JsonSerializer.Serialize(o, Upstream.Json);

    const string AnalystRole =
        """
        You are a senior performance-creative strategist for DTC brands on Meta (Reels/Stories/Feed).
        You break ads down the way a creative director briefs a motion designer: concrete shots, lines, overlays, pacing and sound —
        never generic marketing advice ("use emotion", "be authentic").

        Segment roles (pick exactly one primary role per segment): Hook, Pain, Solution, Demonstration, Proof, Offer, CTA.
        Keep two layers strictly apart:
        - observed: only what is literally heard, written on screen, or shown. Verbatim text. No adjectives that judge quality.
        - interpretation: your inference (mechanism, angle, emotional trigger, storytelling pattern, reusable pattern),
          with "evidence" pointing to the exact observed details and timestamps that support it, and an honest 0–1 confidence.
        Reusable patterns and techniques must be product-agnostic templates using placeholders like [PRODUCT], [PAIN], [RESULT].

        BE TERSE. Observed text is verbatim, but every interpretation field is a phrase of at most 12 words, evidence is at most 20 words,
        reusablePattern and howToReuse are at most 25 words, and the summary is at most 2 short sentences. No filler.
        """;

    // ---------- 1. Competitor video (Gemini) ----------

    /// <summary>Analyzes an uploaded video, reusing a cached result when the same bytes were analyzed before.</summary>
    public async Task<VideoAnalysisResult> AnalyzeVideoAsync(Func<Stream> openVideo, long length, string mime, string name,
        string source, bool force, CancellationToken ct)
    {
        string key;
        await using (var s = openVideo()) key = await AnalysisCache.HashAsync(s, ct);
        return await WithCache(key, source, name, length, force, ct, () =>
            AnalyzeVideoWith(prompt => gemini.AnalyzeVideoAsync<CreativeAnalysis>(openVideo(), length, mime, name, prompt, Schemas.Analysis, ct)));
    }

    public async Task<VideoAnalysisResult> AnalyzeVideoUrlAsync(string url, bool force, CancellationToken ct)
    {
        var uri = VideoLinks.Parse(url);
        if (VideoLinks.YouTubeId(uri) is { } id)
            return await WithCache($"youtube:{id}", url, null, null, force, ct, () =>
                AnalyzeVideoWith(prompt => gemini.AnalyzeYouTubeAsync<CreativeAnalysis>($"https://www.youtube.com/watch?v={id}", prompt, Schemas.Analysis, ct)));

        var file = await links.DownloadAsync(uri, ct);
        try { return await AnalyzeVideoAsync(() => File.OpenRead(file.Path), file.Length, file.MimeType, file.Name, url, force, ct); }
        finally { File.Delete(file.Path); }
    }

    async Task<VideoAnalysisResult> WithCache(string key, string source, string? name, long? size, bool force, CancellationToken ct,
        Func<Task<VideoAnalysisResult>> analyze)
    {
        if (!force && await cache.GetAsync(key, ct) is { } hit)
        {
            log.LogInformation("Cache hit for {Key} (analyzed {When:u}); skipping Gemini.", key, hit.AnalyzedAt);
            return hit.Result;
        }
        var result = await analyze() with { VideoKey = key, AnalyzedAt = DateTime.UtcNow };
        await cache.SaveAsync(key, source, name, size, result, ct);
        return result;
    }

    async Task<VideoAnalysisResult> AnalyzeVideoWith(Func<string, Task<(CreativeAnalysis, string, string, string)>> run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string raw = "", api = "", model = gemini.Model;
        var analysis = await WithRetry(async feedback =>
        {
            var prompt = $"""
                {AnalystRole}

                TASK: Analyze this competitor video ad end-to-end, using both the visuals and the audio track.
                - Split it into contiguous segments covering the full runtime (typically 5–10). Ids C1, C2, … in order.
                - startSec/endSec: seconds from the start, 0.1s precision, taken from the actual video.
                - spokenText: verbatim transcript (if not English, add an English translation in [brackets]).
                - onScreenText: verbatim captions/overlays. visualDescription: shot type, framing, camera move, action, cuts, transitions.
                - audioNotes: music changes, SFX, voice tone.
                - techniques: 5–10 reusable creative techniques (ids T1, T2, …), each tied to sourceSegmentIds and a time range,
                  described so they work for ANY product — strip the competitor's product and brand out of howToReuse.
                - preserve: leave empty. productBenefits: the benefits the competitor claims (observed only).
                - If something is unclear (e.g. inaudible), say so and lower confidence. Never invent content that is not in the video.
                {feedback}
                """;
            (var a, raw, api, model) = await run(prompt);
            return (a, Guardrails.ValidateAnalysis(a, requireTimestamps: true));
        }, a => Guardrails.Normalize(a));
        return new VideoAnalysisResult(analysis, raw, model, api, Math.Round(sw.Elapsed.TotalSeconds, 1));
    }

    // ---------- 2. Winning script (OpenAI) ----------

    public Task<CreativeAnalysis> AnalyzeScriptAsync(string script, CancellationToken ct) =>
        WithRetry(async feedback =>
        {
            var instructions = $"""
                {AnalystRole}

                You are analyzing one of SockLock's own WINNING ad scripts so it can be safely remixed.
                {Guardrails.SockLockFacts}

                - Segment the script with ids W1, W2, … in order. If the script has no labels, infer roles from content.
                - startSec/endSec: estimate from speaking pace (~2.6 words/second, minimum 2s per segment).
                - observed.spokenText = the VO/dialogue lines; onScreenText and visualDescription = what the script specifies (empty if absent);
                  audioNotes = any sound direction (empty if absent).
                - techniques: the 3–5 things this winner already does well (ids WT1, WT2, …).
                - preserve: 3–5 elements that most likely make this script win and must survive any remix — say WHY (e.g. specificity,
                  signature product moment, approved offer wording).
                - productBenefits: only benefits that are stated in the script AND consistent with the approved facts.
                {feedback}
                """;
            var a = await openAi.GenerateJsonAsync<CreativeAnalysis>("script_analysis", Schemas.Analysis, instructions,
                $"WINNING SCRIPT:\n\n{script}", ct);
            return (a, Guardrails.ValidateAnalysis(a, requireTimestamps: false));
        }, a => Guardrails.Normalize(a));

    // ---------- 3. Matching (OpenAI semantic criteria, C# scoring) ----------

    record MatchEnvelope(List<MatchCandidate> Candidates);

    public async Task<MatchResponse> MatchAsync(CreativeAnalysis competitor, CreativeAnalysis winner, CancellationToken ct)
    {
        var instructions = $"""
            You are a creative strategist choosing which competitor techniques to transplant into SockLock's winning script.
            The output is shown as compact cards that a busy marketer scans and picks from, so every text field must be short and concrete.
            {Guardrails.SockLockFacts}

            Return 6–9 candidates. Each pairs ONE competitor technique (and its source segment) with ONE winner segment.
            - 4–7 should be genuinely good ideas, spread across different winner segments where possible.
            - 2 should be the most tempting BAD ideas (needs a discount, testimonial, statistic, or copies the competitor), flagged
              with compatible=false or a risk flag, so the team sees why they're out.

            Score each criterion as an integer 0–10, with a reason of at most 10 words:
            role (same narrative job?), pain (same or adjacent pain?), mechanism (does the persuasion transfer intact?),
            productFit (doable truthfully with ONLY the approved facts? ≤3 if it needs any new claim),
            novelty (how much it adds; 0 = the winner already does this). Don't compute a total; the app does.

            Text fields (strict word limits):
            - techniqueName: ≤5 words, a name a creative team would use.
            - whatToChange: ≤16 words, imperative and specific (shot, line, overlay, sound). This is the card's headline.
            - whatToKeep: ≤10 words.
            - whyTransferable: ≤16 words, the principle that makes it work.
            - hypothesis: ≤20 words, "If …, then … because …".
            - expectedImpact: the single metric most likely to move.
            - riskNotes: ≤12 words, or "" if none.
            unsupportedClaimRisk=true if it needs any claim, proof, testimonial, number or offer beyond the approved facts.
            directCopyRisk=true if it reuses the competitor's actual lines, characters, brand or distinctive scenes.
            Use only ids that exist in the inputs.
            """;
        var input = $"""
            COMPETITOR (segments and reusable techniques):
            {J(CompactForMatching(competitor))}

            SOCKLOCK WINNER (segments and what must be preserved):
            {J(CompactForMatching(winner))}
            """;
        var env = await openAi.GenerateJsonAsync<MatchEnvelope>("creative_matches", Schemas.Matches, instructions, input, ct);
        if (env.Candidates is not { Count: > 0 })
            throw new AppException(502, "No matches were returned.", "Try again, or check that both analyses contain segments.");
        return new MatchResponse(MatchScorer.ScoreAll(env.Candidates, competitor, winner, matching.Value), MatchScorer.Formula);
    }

    /// <summary>Only what matching needs, which keeps the prompt small and fast.</summary>
    static object CompactForMatching(CreativeAnalysis a) => new
    {
        a.PrimaryAngle,
        segments = a.Segments.Select(s => new
        {
            s.Id, s.Role, time = s.StartSec is null ? null : $"{s.StartSec:0.#}-{s.EndSec:0.#}s",
            said = s.Observed.SpokenText, onScreen = s.Observed.OnScreenText, visual = s.Observed.VisualDescription,
            s.Interpretation.Mechanism, s.Interpretation.EmotionalTrigger
        }),
        techniques = a.Techniques.Select(t => new { t.Id, t.Name, t.SourceSegmentIds, t.HowToReuse }),
        preserve = a.Preserve.Select(p => new { p.SegmentId, p.Element })
    };

    // ---------- 4. Generation (OpenAI) ----------

    record VariationEnvelope(List<Variation> Variations);

    // Each parallel request gets its own creative direction, so the variations stay distinct without seeing each other.
    // Each direction dictates a different OPENING, because the hook is what Meta tests first and what makes variations distinct.
    static readonly string[] ModifierDirections =
    [
        "NEW HOOK CONCEPT. Scene 1 is a brand-new hook idea (a question, a bold statement or an unexpected visual) that is not the winner's opening shot.",
        "NEW NARRATIVE ORDER. Scene 1 opens on the payoff (a paired drawer or a clicked pair coming out of the wash), then flashes back to the problem.",
        "NEW VISUAL FORMAT. Scene 1 establishes a distinctive format (split-screen, POV from inside the drawer, or one continuous take) that carries the whole ad.",
        "NEW EMOTIONAL ANGLE. Scene 1 opens on a human emotional beat (a deadpan face, a partner's reaction, a small victory) rather than on socks.",
        "NEW PACING & SOUND. Scene 1 is sound-first: magnet clicks on the beat over a tight macro, before any visual of the problem.",
        "NEW POINT OF VIEW. Scene 1 sets up an unusual narrator (the lonely orphan sock, a laundry-day diary, a partner) who tells the story."
    ];

    static readonly string[] CreatorFormats =
    [
        "Deadpan character-led mini-sketch with props",
        "Single continuous-take challenge with a visible clock",
        "Talking-head explainer that uses a physical prop demo",
        "Reaction or comment-reply format",
        "Day-in-the-life mini-documentary",
        "Satisfying ASMR montage driven by product sounds"
    ];

    public async Task<GenerateResponse> GenerateAsync(GenerateRequest r, CancellationToken ct)
    {
        var creator = r.Mode.Equals("creator", StringComparison.OrdinalIgnoreCase);
        var count = Math.Clamp(r.Count, 1, 10);
        if (r.SelectedMatches is not { Count: > 0 })
            throw new AppException(400, "No competitor techniques selected.", "Select at least one match in Step 3.");

        var directions = creator ? CreatorFormats : ModifierDirections;
        var input = GenerationInput(r);

        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            var lead = r.SelectedMatches[i % r.SelectedMatches.Count];
            var direction = directions[i % directions.Length];
            var others = Enumerable.Range(0, count).Where(j => j != i).Select(j => directions[j % directions.Length]).Distinct();
            var instructions = GenerationInstructions(creator, direction, lead, others);

            var (list, _) = await WithRetryRaw(async feedback =>
            {
                var env = await openAi.GenerateJsonAsync<VariationEnvelope>("creative_variation", Schemas.Variations,
                    instructions + feedback, input, ct, generation: true);
                return (env.Variations, Guardrails.ValidateVariations(env.Variations));
            });
            return list[0] with { Id = $"{(creator ? "N" : "V")}{i + 1}" };
        }).ToList();

        // Keep whatever succeeded; only fail if every request failed.
        try { await Task.WhenAll(tasks); } catch { /* inspected below */ }
        var done = tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToList();
        if (done.Count == 0) throw tasks.First(t => t.IsFaulted).Exception!.InnerException!;

        var finalized = done.Select(v => Guardrails.Finalize(v, r.Competitor)).ToList();
        if (!creator) Guardrails.FlagSimilarHooks(finalized);
        return new GenerateResponse(creator ? "creator" : "modifier", finalized, count - done.Count);
    }

    static string GenerationInstructions(bool creator, string direction, MatchCandidate lead, IEnumerable<string> others) => $"""
        You are SockLock's senior creative writer producing a production-ready brief for motion designers and UGC creators.
        {Guardrails.SockLockFacts}

        {(creator
            ? $"""
              MODE: CREATOR. Write exactly ONE new, original SockLock ad concept, not a rewrite of the winner.
              FORMAT for this concept: {direction}.
              Use the selected competitor techniques as structural inspiration and the winner's insights (pain, preserve list) as grounding.
              """
            : $"""
              MODE: MODIFIER. Write exactly ONE modification of the winning script.
              CREATIVE DIRECTION for this variation: {direction}
              Keep the winner's core: its preserve list, the approved offer wording and the signature product moment.
              """)}
        LEAD TECHNIQUE: build mainly on match {lead.Id} ("{lead.TechniqueName}"); you may add one more selected technique if it fits.
        Other variations in this batch take these directions, so don't drift into them: {string.Join(" | ", others)}

        HARD RULES
        - Scene 1 must follow the direction above and must NOT reuse the winner's opening shot. The winner's preserved hook
          elements (e.g. its specific number) may reappear in a later scene.
        - Use only the approved facts. No new claims, numbers, testimonials, reviews, ratings, guarantees, discounts, free shipping,
          urgency or scarcity. The offer, if stated, is exactly: five pairs for $29, then quarterly deliveries.
        - Adapt techniques, never content: don't reuse the competitor's lines, characters, brand, product or distinctive scenes.
        - inspiredBy cites the selected match(es) used (matchId + competitor timestamp as m:ss–m:ss).
        - 20–35 seconds, 5–7 scenes, 9:16 vertical, captions that work with sound off.
        - voiceOver: natural spoken UGC language. onScreenText: ≤7 words. visualDirection: ≤30 words of shot, framing, action,
          transition and SFX, specific enough to shoot or animate without a call.
        - Return the variations array with exactly one item.
        """;

    static string GenerationInput(GenerateRequest r) => $"""
        ORIGINAL WINNING SCRIPT:
        {r.WinnerScript}

        WHAT MUST BE PRESERVED:
        {J(r.Winner.Preserve.Select(p => new { p.SegmentId, p.Element, p.Reason }))}

        SELECTED COMPETITOR TECHNIQUES (approved by the user):
        {J(r.SelectedMatches.Select(m => new
        {
            matchId = m.Id, m.TechniqueName,
            competitorTimestamp = Timestamp(r.Competitor, m.CompetitorSegmentId),
            howToReuse = r.Competitor.Techniques.FirstOrDefault(t => t.Id == m.CompetitorTechniqueId)?.HowToReuse,
            changeInWinnerSegment = m.WinnerSegmentId, m.WhatToChange, m.WhatToKeep, m.RiskNotes
        }))}
        """;

    static string Timestamp(CreativeAnalysis a, string segmentId) =>
        a.Segments.FirstOrDefault(s => s.Id == segmentId) is { StartSec: { } s, EndSec: { } e }
            ? $"{(int)s / 60}:{(int)s % 60:00}–{(int)e / 60}:{(int)e % 60:00}" : "";

    // ---------- Validation retry ----------

    static async Task<T> WithRetry<T>(Func<string, Task<(T, List<string>)>> call, Func<T, T> normalize, bool allowRetry = true)
    {
        var (result, _) = await WithRetryRaw(call, allowRetry);
        return normalize(result);
    }

    /// <summary>Runs the call; if validation fails, retries once telling the model exactly what was wrong.</summary>
    static async Task<(T, List<string>)> WithRetryRaw<T>(Func<string, Task<(T, List<string>)>> call, bool allowRetry = true)
    {
        var (result, errors) = await call("");
        if (errors.Count == 0) return (result, errors);
        if (allowRetry)
        {
            (result, errors) = await call($"\n\nYOUR PREVIOUS OUTPUT FAILED VALIDATION — fix these issues:\n- {string.Join("\n- ", errors)}");
            if (errors.Count == 0) return (result, errors);
        }
        throw new AppException(502, "The AI output failed validation.", string.Join(" ", errors.Take(5)));
    }
}
