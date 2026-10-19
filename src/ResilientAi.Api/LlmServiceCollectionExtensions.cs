using Microsoft.Extensions.Options;
using Polly;
using ResilientAi.Api.Llm;
using ResilientAi.Api.Summarization;
using ResilientAi.Api.Telemetry;

namespace ResilientAi.Api;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the model client, its resilience pipeline, telemetry, and the
    /// summarisation service. Program and the tests both call this, so the tests
    /// exercise the production pipeline rather than a copy of it.
    /// </summary>
    public static IServiceCollection AddLlmSummarization(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .Configure(o => o.ApiKey = configuration["ANTHROPIC_API_KEY"]);

        services.AddSingleton<LlmTelemetry>();

        services.AddHttpClient<ILlmClient, AnthropicLlmClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com/");
            // Polly owns every timeout; a finite value here would race it.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.AddResiliencePipeline(LlmResilience.PipelineName, (builder, context) =>
            LlmResilience.Configure(
                builder,
                context.ServiceProvider.GetRequiredService<IOptions<LlmOptions>>().Value.Resilience));

        services.AddScoped<SummarizationService>();

        services.AddHealthChecks()
            .AddCheck<LlmConfiguredHealthCheck>("llm-configured", tags: ["ready"]);

        return services;
    }
}
