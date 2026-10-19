# Design decisions

Each decision states what was chosen, the alternative, and what it costs. This
is the document to read in an interview.

## 1. The Result pattern for expected failures

**Decision.** The service returns `Result<T>` with an `ErrorKind` for outcomes
that are part of normal operation (bad input, model unavailable). Exceptions are
reserved for genuinely exceptional situations.

**Rejected.** Throwing for validation and unavailability and catching at the
edge. It works, but it uses exceptions for control flow, makes the failure modes
invisible in the signature, and is slower on the failure path.

**Cost.** A little more ceremony than `throw`: the caller must check the result,
and the transport maps `ErrorKind` to a status code. That explicitness is the
point; the mapping lives in exactly one place (`Program.cs`).

## 2. Resilience as an explicit pipeline (Polly)

**Decision.** The model call runs inside one named Polly pipeline (`"llm"`,
defined once in `LlmResilience.Configure` and registered with
`AddResiliencePipeline`, so the tests resolve the same pipeline as production).
From the outside in:

1. a **total timeout** (60s) that bounds the whole request, retries included;
2. a **retry** on `TransientLlmException` (5xx including 529 overloaded, 429,
   network errors) and on a per-attempt `TimeoutRejectedException`: 2 retries,
   so 3 attempts in total, exponential backoff from 200ms with jitter. When the
   provider sends `Retry-After` on a 429 or 529, that delay is used instead,
   capped at 10s;
3. a **per-attempt timeout** (30s).

`HttpClient.Timeout` is disabled so Polly owns every timeout; a finite value
would race the pipeline and surface as a different exception type. Other 4xx
and unparseable bodies are `PermanentLlmException` and are not retried. A
missing API key is `LlmNotConfiguredException`, also not retried. All values
are configurable under `Llm:Resilience`.

**Rejected.** No retries (one provider hiccup becomes a user-visible error) and
retry-on-everything (retrying a 400 just wastes time and money). A single
timeout, either inside the retry (no bound on the whole request) or outside it
(one hung attempt eats the whole budget).

**Cost.** Retries add latency to the failure path and duplicate load on the
provider. With the defaults, a request gives up after at most 60s. Two hung
attempts consume that budget, so the third attempt only happens when earlier
failures were fast. The right values depend on a real error budget, which is
noted as an extension.

**Missing API key.** The service starts without one rather than failing at
startup: `/health/ready` reports unhealthy so an orchestrator holds traffic, and
`/summarize` returns 503 with `"the model provider is not configured"`. Failing
fast at startup was rejected because it would also take down liveness and make
the misconfiguration harder to see from outside.

**Soft spot, stated honestly.** There is no circuit breaker yet, so under a
sustained provider outage every request still pays the full retry cost before
failing. A breaker is the natural next layer; it was left out to keep the
baseline small rather than ship a half-tuned one.

## 3. Token and cost metrics as first-class

**Decision.** Telemetry follows the OpenTelemetry GenAI semantic conventions:

- one `chat {model}` client span per summarisation (`ActivitySource`
  `ResilientAi`) with `gen_ai.operation.name`, `gen_ai.provider.name`
  (`anthropic`), `gen_ai.request.model`, `gen_ai.response.model`,
  `gen_ai.usage.input_tokens` and `gen_ai.usage.output_tokens`, plus
  `error.type` and an error status on failure. Each HTTP attempt shows up as a
  child span through HttpClient instrumentation, so retries are visible;
- `gen_ai.client.token.usage` (tagged `gen_ai.token.type` input/output) and
  `gen_ai.client.operation.duration` in seconds (tagged `error.type` on
  failure). Duration covers the whole pipeline, retries included;
- `resilientai.llm.cost` in USD, tagged with the model, computed from per-model
  list prices in `Llm:Pricing`. A model with no price records no cost and logs
  a warning, rather than reporting a wrong number.

Prompt and completion text are never put on spans or metrics: they are user
content.

**Rejected.** Logging token counts as text, or not tracking cost at all. An LLM
service whose spend you cannot graph is hard to operate. Hardcoded prices were
also rejected: they silently go wrong when the model changes.

**Cost.** Prices are configuration, so they can still drift from the provider's
list. They were checked against claude.com/pricing on 2026-10-20 and must be
re-checked when prices or the model change. Only base-tier prices are
modelled: Claude Haiku 5.5 charges more above 100K prompt tokens, which the
20,000-character input cap keeps this service well below. Cache and batch
discounts are not modelled.

## 4. Export only when a collector is configured

**Decision.** OTLP export is enabled only when `OTEL_EXPORTER_OTLP_ENDPOINT` is
set. Instrumentation always runs; shipping is opt-in.

**Why.** So local runs and the test suite do not spam a collector that is not
there, while production turns on export with one environment variable.

## 5. The model behind an interface

**Decision.** `ILlmClient` is the only thing the service knows about the
provider. The real implementation (`AnthropicLlmClient`) uses a typed
`HttpClient`; tests inject a fake that can succeed, fail-then-succeed, or always
fail.

**Why.** It makes the safety-critical behaviour (retry, error mapping, status
codes) testable with no network and no API key, and it means swapping providers
touches one class.

**Cost.** One layer of indirection. Cheap, and it pays for itself the first time
you write a test.

## Left as extension points

Deliberately not built, so the baseline stays small and honest. Each is a real
next step with a real tradeoff to reason about:

- a circuit breaker tuned to an error budget (see section 2's soft spot)
- streaming responses (changes the contract and the metrics)
- a response cache keyed on input (cost win, staleness risk)
- an eval harness for summary quality (the model's job, not unit-testable)
- handling `stop_reason` (`refusal`, `max_tokens`): today a 200 with a refusal
  or a truncated answer is returned as a successful summary
