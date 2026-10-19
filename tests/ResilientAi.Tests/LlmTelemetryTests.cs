using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using ResilientAi.Api.Summarization;
using ResilientAi.Api.Telemetry;
using Xunit;

namespace ResilientAi.Tests;

public class LlmTelemetryTests
{
    private static readonly Dictionary<string, string?> SonnetPrice = new()
    {
        ["Llm:Pricing:claude-sonnet-5-5:InputUsdPerMTok"] = "2",
        ["Llm:Pricing:claude-sonnet-5-5:OutputUsdPerMTok"] = "10",
    };

    [Fact]
    public async Task Emits_a_chat_span_with_gen_ai_attributes()
    {
        const string model = "span-test-model";
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LlmTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var service = TestServices.Service(
            new FakeLlmClient(model: model),
            new Dictionary<string, string?> { ["Llm:Model"] = model });
        await service.SummarizeAsync("hello", CancellationToken.None);

        Activity span;
        lock (stopped) span = Assert.Single(stopped, a => a.DisplayName == $"chat {model}");
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("chat", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("anthropic", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal(model, span.GetTagItem("gen_ai.request.model"));
        Assert.Equal(100, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(20, span.GetTagItem("gen_ai.usage.output_tokens"));
    }

    [Fact]
    public async Task Records_tokens_by_type_and_cost_tagged_with_the_model()
    {
        var (provider, measurements, listener) = Listen(new FakeLlmClient(), SonnetPrice);
        using var _ = listener;
        await provider.GetRequiredService<SummarizationService>().SummarizeAsync("hello", CancellationToken.None);

        var cost = Assert.Single(measurements, m => m.Name == "resilientai.llm.cost");
        Assert.Equal((100 * 2.0 + 20 * 10.0) / 1_000_000, cost.Value, 12);
        Assert.Equal("claude-sonnet-5-5", cost.Tags["gen_ai.response.model"]);

        var tokens = measurements.Where(m => m.Name == "gen_ai.client.token.usage").ToList();
        Assert.Contains(tokens, m => m.Value == 100 && (string?)m.Tags["gen_ai.token.type"] == "input");
        Assert.Contains(tokens, m => m.Value == 20 && (string?)m.Tags["gen_ai.token.type"] == "output");
        Assert.Single(measurements, m => m.Name == "gen_ai.client.operation.duration");
    }

    [Fact]
    public async Task Records_no_cost_for_a_model_without_a_price()
    {
        var (provider, measurements, listener) = Listen(new FakeLlmClient(model: "claude-unpriced"), SonnetPrice);
        using var _ = listener;
        var result = await provider.GetRequiredService<SummarizationService>()
            .SummarizeAsync("hello", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(measurements, m => m.Name == "resilientai.llm.cost");
        Assert.Contains(measurements, m => m.Name == "gen_ai.client.token.usage");
    }

    [Fact]
    public async Task Tags_failed_operations_with_the_error_type()
    {
        var (provider, measurements, listener) = Listen(new FakeLlmClient(alwaysHang: true), SonnetPrice);
        using var _ = listener;
        await provider.GetRequiredService<SummarizationService>().SummarizeAsync("hello", CancellationToken.None);

        var duration = Assert.Single(measurements, m => m.Name == "gen_ai.client.operation.duration");
        Assert.Equal("timeout", duration.Tags["error.type"]);
        Assert.DoesNotContain(measurements, m => m.Name == "gen_ai.client.token.usage");
    }

    private sealed record Measurement(string Name, double Value, Dictionary<string, object?> Tags);

    private static (ServiceProvider, List<Measurement>, MeterListener) Listen(
        FakeLlmClient fake, Dictionary<string, string?> settings)
    {
        var provider = TestServices.Build(fake, settings);
        var factory = provider.GetRequiredService<IMeterFactory>();
        var measurements = new List<Measurement>();

        var listener = new MeterListener
        {
            // Scope pins the listener to this provider's meters, so parallel tests do not leak in.
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == factory)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        void Add(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var t in tags)
                copy[t.Key] = t.Value;
            lock (measurements) measurements.Add(new Measurement(i.Name, v, copy));
        }
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        listener.Start();

        // Instruments are created lazily with the telemetry singleton; force it so they publish.
        provider.GetRequiredService<LlmTelemetry>();
        return (provider, measurements, listener);
    }
}
