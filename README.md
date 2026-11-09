# Creative Remix Engine (SockLock)

Turns competitor video ads into reusable techniques, matches them to our winning scripts with an explainable score, and generates production-ready creative briefs.

## Run

**Docker (app + PostgreSQL cache):**

```bash
cp .env.example .env          # add GEMINI_API_KEY and OPENAI_API_KEY
docker compose up --build     # http://localhost:5080
```

**Locally:**

```bash
docker compose up -d db       # optional: the video analysis cache (Postgres on localhost:5433)
dotnet run                    # http://localhost:5080
```

Without Postgres the app still works; the cache is just off (the top bar shows "Cache off").

Click **Load Demo** to walk through the full flow with clearly labeled sample results. No API keys are needed for the demo.

## API keys

Environment variables take precedence over config:

| Provider | Env var | Config fallback |
|---|---|---|
| Gemini (video analysis) | `GEMINI_API_KEY` | `Gemini:ApiKey` |
| OpenAI (script analysis, matching, generation) | `OPENAI_API_KEY` | `OpenAI:ApiKey` |

Don't commit keys. For local runs, put them in `appsettings.Local.json` (git- and docker-ignored) or env vars; for Docker, in `.env`.
The Docker build also blanks any `ApiKey` values, so keys never end up inside the image. Config changes apply without a restart.

## Models (`appsettings.json`)

- `Gemini:Model`: default `gemini-3.6-flash` (about 10s per short ad; `gemini-3.8-flash` was taking 4+ minutes under load).
  `Gemini:FallbackModels` are tried in order if the primary is overloaded or exceeds `Gemini:RequestTimeoutSeconds` (300).
  `Gemini:MediaResolution` (`low`/`medium`/`high`) trades speed for on-screen-text accuracy. `Gemini:Api` is `interactions` (default) or `generateContent`; the client falls back automatically if the Interactions endpoint is unavailable.
- `OpenAI:Model`: used for script analysis and matching. Default `gpt-6-luna`.
- `OpenAI:GenerationModel`: used for creative generation. Set to `gpt-6.1-terra` for stronger copy; falls back to `OpenAI:Model` if empty.
- `OpenAI:ReasoningEffort`: optional (`low` / `medium` / `high`) for models that support it.
- `Matching:MinScore` (55) and `Matching:MinProductFit` (4): acceptance thresholds.

## Video analysis cache

Each analyzed video is stored in PostgreSQL (`video_analyses`) under its SHA-256 content hash, or `youtube:{id}` for YouTube links,
together with the structured analysis, Gemini's raw output, the model and the timing. Analyzing the same video again returns the
stored result instantly; "Re-analyze" in the UI (or `?force=true` / `"force": true`) bypasses it.

## How it works

1. **Video → Gemini.** Files API upload, then structured JSON: segments (Hook / Pain / Solution / Demonstration / Proof / Offer / CTA) with timestamps. Each segment keeps *observed* content (verbatim VO, on-screen text, visuals) separate from *AI interpretation* (mechanism, angle, trigger, reusable pattern), and every interpretation carries its evidence and a confidence score. The raw model output is shown in the UI.
2. **Script → OpenAI**, using the same segment model, plus the elements worth preserving.
3. **Matching.** GPT scores Role, Pain, Mechanism, Product Fit and Novelty from 0–10, each with a reason. C# computes `0.25·Role + 0.20·Pain + 0.25·Mechanism + 0.20·ProductFit + 0.10·Novelty` and rejects incompatible matches, unsupported claims, direct copying, and low product fit (`Services/MatchScorer.cs`).
4. **Generation.** Modifier mode produces N distinct remixes; Creator mode produces new concepts. Only user-selected matches are used. Each output goes through a deterministic compliance check for invented prices, discounts, guarantees, testimonials or urgency, and for 6-word overlaps with the competitor's lines (`Services/Guardrails.cs`).

All LLM calls use strict JSON schemas (`Services/Schemas.cs`). Invalid output is retried once with the validation errors fed back to the model.
