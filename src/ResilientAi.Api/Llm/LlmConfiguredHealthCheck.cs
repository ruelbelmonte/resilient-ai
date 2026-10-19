using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ResilientAi.Api.Llm;

/// <summary>Readiness: the service cannot do its job without a provider API key.</summary>
public sealed class LlmConfiguredHealthCheck : IHealthCheck
{
    private readonly LlmOptions _options;

    public LlmConfiguredHealthCheck(IOptions<LlmOptions> options) => _options = options.Value;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.IsNullOrEmpty(_options.ApiKey)
            ? HealthCheckResult.Unhealthy("ANTHROPIC_API_KEY is not configured")
            : HealthCheckResult.Healthy());
}
