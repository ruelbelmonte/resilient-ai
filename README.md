# resilient-ai

A small .NET service that does one thing and does it like production code: it
exposes an LLM-backed endpoint that stays up when the model provider does not.
The summarisation feature is almost beside the point. The point is everything
around the model call: retries with backoff, a timeout, typed error flow instead
of exceptions leaking to callers, health checks, and first-class observability
including token and cost metrics.

Most LLM demos show the happy path against a provider that never fails. Real
providers rate-limit, time out, and return 5xx. This shows how a .NET service
handles that without falling over or hiding the cost.

## The problem

An endpoint that calls an LLM provider has to survive things a normal CRUD
endpoint never sees: 429s under load, slow or hanging requests, transient 5xx,
and a per-request bill you cannot see unless you measure it. Ignore these and
the service is a demo; handle them and it is something you would actually deploy.

## Design decisions and tradeoffs

The decisions, the alternatives, and what each costs are in
[DESIGN.md](./DESIGN.md). Read that first; it is where the engineering judgement
lives.

## Running it

```bash
dotnet test                                   # full suite, no API key, no network
dotnet run --project src/ResilientAi.Api      # needs ANTHROPIC_API_KEY for a real call
```

```bash
export ANTHROPIC_API_KEY=...
curl -s localhost:5000/summarize -H 'content-type: application/json' \
  -d '{"text":"some long text to summarise"}'
curl -s localhost:5000/health
```

The test suite needs neither an API key nor a network: the model is behind an
interface (`ILlmClient`) and replaced with a fake, so resilience, error mapping,
and the HTTP contract are all verified deterministically.

To see telemetry exported, set `OTEL_EXPORTER_OTLP_ENDPOINT` to a collector;
without it, instrumentation still runs but nothing is shipped, so local runs stay
quiet.

## Architecture

```
POST /summarize
   -> validate input            -> 400 on bad input (Result, not exception)
   -> resilience pipeline       -> retry transient failures, then timeout
        -> ILlmClient            (AnthropicLlmClient in prod, fake in tests)
   -> record metrics            -> requests, duration, tokens, cost (by outcome)
   -> map Result -> HTTP        -> 200 | 400 | 503
GET /health                     -> liveness
```

Transport (minimal API in `Program.cs`) holds no logic; `SummarizationService`
does the work and is unit-tested directly, while the endpoints are covered by
`WebApplicationFactory` integration tests.

## Status

Runnable and tested: 10 tests covering the Result type, retry-then-succeed and
retry-exhaustion, validation, the 200/400/503 contract, and health. CI runs
format, build (warnings as errors), and tests.

Deliberately left as extension points (and good interview talking points):
streaming responses, a response cache, a circuit breaker tuned to a real error
budget, and an eval of summary quality. See DESIGN.md.
