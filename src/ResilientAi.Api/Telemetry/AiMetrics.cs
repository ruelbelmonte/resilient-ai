using System.Diagnostics.Metrics;

namespace ResilientAi.Api.Telemetry;

/// <summary>
/// Application metrics for the AI path. Token and cost counters are the point:
/// an LLM service you cannot see the spend of is not production-ready. Built on
/// <see cref="IMeterFactory"/> so the meter integrates with OpenTelemetry.
/// </summary>
public sealed class AiMetrics
{
    public const string MeterName = "ResilientAi";

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _durationMs;
    private readonly Counter<long> _inputTokens;
    private readonly Counter<long> _outputTokens;
    private readonly Histogram<double> _costUsd;

    public AiMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>("ai.requests", description: "AI requests by outcome");
        _durationMs = meter.CreateHistogram<double>("ai.request.duration", "ms");
        _inputTokens = meter.CreateCounter<long>("ai.tokens.input");
        _outputTokens = meter.CreateCounter<long>("ai.tokens.output");
        _costUsd = meter.CreateHistogram<double>("ai.cost", "usd");
    }

    public void Record(double durationMs, int inputTokens, int outputTokens, double costUsd, string outcome)
    {
        var tag = new KeyValuePair<string, object?>("outcome", outcome);
        _requests.Add(1, tag);
        _durationMs.Record(durationMs, tag);
        _inputTokens.Add(inputTokens);
        _outputTokens.Add(outputTokens);
        _costUsd.Record(costUsd);
    }
}
