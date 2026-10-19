using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResilientAi.Api;
using ResilientAi.Api.Llm;
using ResilientAi.Api.Summarization;

namespace ResilientAi.Tests;

/// <summary>
/// Builds the production registrations with only the model swapped and the
/// timings shrunk, so tests run the real pipeline in milliseconds.
/// </summary>
public static class TestServices
{
    public static readonly Dictionary<string, string?> FastTimings = new()
    {
        ["Llm:Resilience:BaseDelay"] = "00:00:00",
        ["Llm:Resilience:AttemptTimeout"] = "00:00:00.100",
        ["Llm:Resilience:TotalTimeout"] = "00:00:05",
    };

    public static ServiceProvider Build(ILlmClient llm, IDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>(FastTimings);
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            settings[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging().AddMetrics();
        services.AddLlmSummarization(configuration);
        services.RemoveAll<ILlmClient>();
        services.AddSingleton(llm);
        return services.BuildServiceProvider();
    }

    public static SummarizationService Service(ILlmClient llm, IDictionary<string, string?>? overrides = null) =>
        Build(llm, overrides).GetRequiredService<SummarizationService>();
}
