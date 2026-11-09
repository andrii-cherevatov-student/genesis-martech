# Creative Remix Engine

A creative-intelligence workspace for **SockLock** (magnetic socks sold through Meta ads). It analyzes competitor video ads,
extracts reusable advertising techniques, matches them to our proven winning scripts with an explainable score, and generates
production-ready script variations for motion designers.

**Why:** the team ships about 240 ad modifications a month and needs about 500. The north-star metric is
**approved creative variations per hour**, which the app tracks live in the top bar.

## Workflow

1. **Creative inputs.** Drop a competitor ad (MP4), or paste a direct video link or a YouTube link, with a preview.
   Paste one of our winning scripts, or use the sample.
2. **Breakdown.** Gemini watches and listens to the video. Each segment (Hook, Pain, Solution, Demonstration, Proof, Offer, CTA)
   has timestamps and keeps two layers apart: **observed** (verbatim voice-over, on-screen text, visuals, audio) and
   **AI interpretation** (mechanism, angle, emotional trigger, reusable pattern), each with evidence and a confidence score.
   A clickable timeline seeks the video. Our script gets the same breakdown, plus the elements that must be preserved.
3. **Matching.** Competitor techniques are grouped by the part of our script they'd change. Each is one compact row: a frame from
   the competitor video (click to play the moment), the proposed change, the expected impact ("↑ Hold rate") and a score.
   The marketer ticks what to borrow; ideas that need unsupported claims or copy the competitor are ruled out automatically.
4. **Generate & review.** *Modifier* mode writes N distinct remixes of the winner; *Creator* mode writes new concepts.
   Each card shows the hook, scenes (voice-over, on-screen text, visual direction), an original-vs-modified comparison,
   the full script, and the competitor techniques that inspired it. Approve, copy, or export everything as JSON.

**Load Demo** runs the whole flow on clearly labeled sample data, with no API keys and no API calls.

## Quick start

**Docker (app + PostgreSQL):**

```bash
cp .env.example .env          # add GEMINI_API_KEY and OPENAI_API_KEY
docker compose up --build     # http://localhost:5080
```

**Locally (.NET 10 SDK):**

```bash
docker compose up -d db       # optional: video analysis cache (Postgres on localhost:5433)
dotnet run                    # http://localhost:5080
```

Without Postgres the app still works; the cache is just off (the top bar shows "Cache off").

## API keys

| Provider | Used for | Env var | Config fallback |
|---|---|---|---|
| Google Gemini | Video analysis | `GEMINI_API_KEY` | `Gemini:ApiKey` |
| OpenAI | Script analysis, matching, generation | `OPENAI_API_KEY` | `OpenAI:ApiKey` |

Never commit keys. Locally, use env vars or `appsettings.Local.json` (git- and docker-ignored, loaded automatically).
With Docker, use `.env`. The Docker build also blanks any `ApiKey` values, so keys never end up inside the image.

## Configuration (`appsettings.json`)

Changes apply without a restart.

| Setting | Default | Notes |
|---|---|---|
| `Gemini:Model` | `gemini-3.6-flash` | About 10s for a short ad. `gemini-3.8-flash` took 4+ minutes under load in testing. |
| `Gemini:FallbackModels` | `gemini-3.5-flash-lite`, `gemini-3.8-flash` | Tried in order when the primary is overloaded (503/429) or times out. |
| `Gemini:RequestTimeoutSeconds` | `300` | Per analysis call. |
| `Gemini:MediaResolution` | model default | `low` / `medium` / `high`: lower is faster but reads small captions less reliably. |
| `Gemini:Api` | `interactions` | Or `generateContent`; falls back automatically if Interactions is unavailable. |
| `OpenAI:Model` | `gpt-6-luna` | Script analysis and matching. |
| `OpenAI:GenerationModel` | `gpt-6.1-terra` | Creative generation; falls back to `OpenAI:Model`. |
| `OpenAI:ReasoningEffort` | `low` | Also `OpenAI:GenerationReasoningEffort`. |
| `Matching:MinScore` / `MinProductFit` | `55` / `4` | Acceptance thresholds. |
| `ConnectionStrings:Postgres` | `localhost:5433` | Remove it to run without the cache. |

## How it works

- **Structured outputs everywhere.** Every LLM call uses a strict JSON schema (`Services/Schemas.cs`). The output is validated,
  and an invalid response is retried once with the validation errors fed back.
- **Explainable matching.** GPT scores Role, Pain, Mechanism, Product Fit and Novelty (0–10, with a short reason each).
  The score is computed in C#, not by the model (`Services/MatchScorer.cs`):

  `Match = 0.25 × Role + 0.20 × Pain + 0.25 × Mechanism + 0.20 × Product Fit + 0.10 × Novelty`

  Matches are rejected for incompatibility, unsupported claims, direct copying, low product fit, or a score below threshold.
- **Fast, distinct generation.** Each variation is a separate parallel request with its own creative direction
  (new hook, new narrative order, new visual format, new emotional angle, sound-first, new point of view),
  so 5 variations take about 10s and open differently.
- **Guardrails.** Only approved SockLock facts are allowed: magnets on the cuffs keep pairs together; five pairs for $29, then
  quarterly deliveries. A deterministic check flags invented prices, discounts, guarantees, testimonials, urgency, and any
  6-word overlap with the competitor's lines (`Services/Guardrails.cs`).
- **Video analysis cache.** Results are stored in PostgreSQL (`video_analyses`) under the video's SHA-256 hash, or `youtube:{id}`,
  with the raw Gemini output. The same video is instant the second time; **Re-analyze** bypasses the cache.

## API

| Endpoint | Purpose |
|---|---|
| `GET /api/status` | Configured providers, models, cache state |
| `GET /api/demo` | Sample data for demo mode |
| `POST /api/analyze/video` | Multipart `file`; `?force=true` skips the cache |
| `POST /api/analyze/video-url` | `{ "url", "force" }`: direct video link or YouTube |
| `POST /api/analyze/script` | `{ "script" }` |
| `POST /api/match` | `{ "competitor", "winner" }` → scored matches |
| `POST /api/generate` | `{ "mode", "count", "winnerScript", "winner", "competitor", "selectedMatches" }` |

## Project layout

```
Program.cs                 Endpoints, error handling, startup
Services/
  GeminiClient.cs          Files API upload, Interactions/generateContent, retries, fallbacks, timeouts
  OpenAiClient.cs          Responses API with strict JSON schemas
  CreativeEngine.cs        Prompts and the four workflow steps
  MatchScorer.cs           Weighted score and rejection rules
  Guardrails.cs            Validation, compliance checks, approved facts
  AnalysisCache.cs         PostgreSQL cache keyed by video hash
  VideoLinks.cs            Link downloads (with a private-network guard) and YouTube ids
  Schemas.cs, Models.cs    Shared semantic model
Data/demo.json             Labeled sample data (fictional competitor)
wwwroot/                   Vanilla JS front end (no build step)
```

## Notes

- Ad Library and TikTok *pages* don't expose the video file. Download the ad and drop it in, or use a direct video link.
- Model names reflect the providers' docs as of November 2026; change them in config as new models ship.
