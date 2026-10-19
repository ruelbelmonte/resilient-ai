# resilient-ai

[![ci](https://github.com/ruelbelmonte/resilient-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/ruelbelmonte/resilient-ai/actions/workflows/ci.yml)

A small .NET service that exposes an LLM-backed endpoint designed to degrade
cleanly when the model provider does not cooperate.
The summarisation feature is almost beside the point. The point is everything
around the model call: retries with backoff, per-attempt and total timeouts,
typed error flow instead of exceptions leaking to callers, liveness and readiness
checks, and observability including GenAI spans, token and cost metrics.

Most LLM demos show the happy path against a provider that never fails. Real
providers rate-limit, time out, and return 5xx. This shows how a .NET service
handles that without falling over or hiding the cost.

## The problem

An endpoint that calls an LLM provider has to survive things a normal CRUD
endpoint never sees: 429s under load, slow or hanging requests, transient 5xx,
and a per-request bill you cannot see unless you measure it. Ignore these and
the service is a demo; handling them is the starting point for something deployable.

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
   -> "chat {model}" span
   -> resilience pipeline       -> total timeout > retry > per-attempt timeout
        -> ILlmClient            (AnthropicLlmClient in prod, fake in tests)
   -> record metrics            -> duration, tokens by type, cost by model
   -> map Result -> HTTP        -> 200 | 400 | 502 | 503
GET /health/live                -> liveness
GET /health/ready               -> readiness (API key configured)
```

Transport (minimal API in `Program.cs`) holds no logic; `SummarizationService`
does the work. Program and the tests register everything through the same
`AddLlmSummarization` call, so the tests run the production pipeline with only
the timings shrunk. `AnthropicLlmClient` is tested against a stub
`HttpMessageHandler`, and the endpoints by `WebApplicationFactory`.

## Status

Runnable and tested: 39 tests covering the Result type, retries, per-attempt and
total timeouts, Retry-After, provider-error mapping, the 200/400/502/503
contract, liveness and readiness, GenAI span and metric emission, and the
client's handling of 429, 529, 4xx and malformed responses. CI runs format,
build (warnings as errors), and tests. Not yet exercised against the live
provider in CI.

Deliberately left as extension points (and good interview talking points):
streaming responses, a response cache, a circuit breaker tuned to a real error
budget, and an eval of summary quality. See DESIGN.md.
