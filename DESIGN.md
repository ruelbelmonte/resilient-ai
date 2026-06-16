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

**Decision.** The model call runs inside a Polly pipeline: retry on
`TransientLlmException` (3 attempts, exponential backoff from 200ms) then a 30s
timeout. Only transient failures (5xx, 429, network timeouts) are retried;
everything else fails fast.

**Rejected.** No retries (one provider hiccup becomes a user-visible error) and
retry-on-everything (retrying a 400 just wastes time and money).

**Cost.** Retries add latency to the failure path (up to ~1.4s of backoff before
giving up) and, in theory, duplicate load on the provider. The attempt count and
backoff are tunable constants; the right values depend on a real error budget,
which is noted as an extension.

**Soft spot, stated honestly.** There is no circuit breaker yet, so under a
sustained provider outage every request still pays the full retry cost before
failing. A breaker is the natural next layer; it was left out to keep the
baseline small rather than ship a half-tuned one.

## 3. Token and cost metrics as first-class

**Decision.** Every request records request count, duration, input/output
tokens, and estimated USD cost, tagged by outcome, via `IMeterFactory` so the
meter flows through OpenTelemetry. Traces use ASP.NET Core instrumentation plus
an app activity source.

**Rejected.** Logging token counts as text, or not tracking cost at all. An LLM
service whose spend you cannot graph is not operable; cost is a first-class
signal here, not an afterthought.

**Cost.** A small amount of wiring and the discipline of keeping the pricing
constants in sync with the model. Pricing drift is the obvious failure mode and
is called out in code.

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
