using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace ResilientAi.Api.Llm;

/// <summary>
/// The one definition of the model-call pipeline, registered by name so the app
/// and the tests resolve the same thing. See DESIGN.md section 2.
/// </summary>
public static class LlmResilience
{
    public const string PipelineName = "llm";

    public static ResiliencePipelineBuilder Configure(
        ResiliencePipelineBuilder builder, LlmResilienceOptions options) =>
        builder
            .AddTimeout(options.TotalTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<TransientLlmException>()
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.BaseDelay,
                DelayGenerator = args => ValueTask.FromResult(
                    RetryAfter(args.Outcome.Exception, options.MaxRetryAfter)),
            })
            .AddTimeout(options.AttemptTimeout);

    // Null falls back to the exponential backoff. The cap keeps a hostile or
    // buggy header from parking the request; the total timeout bounds it anyway.
    private static TimeSpan? RetryAfter(Exception? exception, TimeSpan cap) =>
        exception is TransientLlmException { RetryAfter: { } hint }
            ? (hint < cap ? hint : cap)
            : null;
}
