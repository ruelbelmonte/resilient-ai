using System.Diagnostics;
using System.Diagnostics.Metrics;
using ResilientAi.Api.Llm;

namespace ResilientAi.Api.Telemetry;

/// <summary>
/// Spans and metrics for the model call, named per the OpenTelemetry GenAI
/// semantic conventions. Prompt and completion text are deliberately never
/// recorded: they are user content. See DESIGN.md section 3.
/// </summary>
public sealed class LlmTelemetry
{
    public const string SourceName = "ResilientAi";

    private const string Operation = "chat";
    private const string Provider = "anthropic";

    private static readonly ActivitySource Source = new(SourceName);

    private readonly Histogram<long> _tokenUsage;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _costUsd;

    public LlmTelemetry(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(SourceName);
        _tokenUsage = meter.CreateHistogram<long>(
            "gen_ai.client.token.usage", "{token}", "Tokens used per model operation");
        _duration = meter.CreateHistogram<double>(
            "gen_ai.client.operation.duration", "s", "Model operation duration, including retries");
        _costUsd = meter.CreateHistogram<double>(
            "resilientai.llm.cost", "USD", "Estimated cost from configured list prices");
    }

    public Activity? StartChat(string requestModel)
    {
        var activity = Source.StartActivity($"{Operation} {requestModel}", ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", Operation);
        activity?.SetTag("gen_ai.provider.name", Provider);
        activity?.SetTag("gen_ai.request.model", requestModel);
        return activity;
    }

    public void RecordSuccess(
        Activity? activity, string requestModel, LlmResponse response, TimeSpan elapsed, double? costUsd)
    {
        activity?.SetTag("gen_ai.response.model", response.Model);
        activity?.SetTag("gen_ai.usage.input_tokens", response.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", response.OutputTokens);

        var tags = BaseTags(requestModel);
        tags.Add("gen_ai.response.model", response.Model);
        _duration.Record(elapsed.TotalSeconds, tags);

        var input = tags;
        input.Add("gen_ai.token.type", "input");
        _tokenUsage.Record(response.InputTokens, input);
        var output = tags;
        output.Add("gen_ai.token.type", "output");
        _tokenUsage.Record(response.OutputTokens, output);

        if (costUsd is { } cost)
            _costUsd.Record(cost, tags);
    }

    public void RecordFailure(Activity? activity, string requestModel, TimeSpan elapsed, string errorType)
    {
        activity?.SetTag("error.type", errorType);
        activity?.SetStatus(ActivityStatusCode.Error);

        var tags = BaseTags(requestModel);
        tags.Add("error.type", errorType);
        _duration.Record(elapsed.TotalSeconds, tags);
    }

    private static TagList BaseTags(string requestModel) => new()
    {
        { "gen_ai.operation.name", Operation },
        { "gen_ai.provider.name", Provider },
        { "gen_ai.request.model", requestModel },
    };
}
