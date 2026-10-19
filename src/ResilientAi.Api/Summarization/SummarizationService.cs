using System.Diagnostics;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Polly.Timeout;
using ResilientAi.Api.Llm;
using ResilientAi.Api.Telemetry;

namespace ResilientAi.Api.Summarization;

public sealed record SummaryResult(string Summary, int InputTokens, int OutputTokens);

/// <summary>
/// Summarises text via the model, wrapped in the resilience pipeline and the
/// Result pattern. Every expected failure (transient errors that outlast the
/// pipeline, timeouts, provider rejections, missing configuration) comes back as
/// a typed Result, not an exception. See DESIGN.md sections 1 to 3.
/// </summary>
public sealed class SummarizationService
{
    private const int MaxInputChars = 20_000;

    private readonly ILlmClient _llm;
    private readonly ResiliencePipeline _pipeline;
    private readonly LlmTelemetry _telemetry;
    private readonly LlmOptions _options;
    private readonly ILogger<SummarizationService> _logger;

    public SummarizationService(
        ILlmClient llm,
        ResiliencePipelineProvider<string> pipelines,
        LlmTelemetry telemetry,
        IOptions<LlmOptions> options,
        ILogger<SummarizationService> logger)
    {
        _llm = llm;
        _pipeline = pipelines.GetPipeline(LlmResilience.PipelineName);
        _telemetry = telemetry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<SummaryResult>> SummarizeAsync(string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result<SummaryResult>.Failure(ErrorKind.Validation, "text must not be empty");
        if (text.Length > MaxInputChars)
            return Result<SummaryResult>.Failure(
                ErrorKind.Validation, $"text exceeds {MaxInputChars} characters");

        var prompt = $"Summarise the following text in three sentences:\n\n{text}";
        var model = _options.Model;
        using var activity = _telemetry.StartChat(model);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await _pipeline.ExecuteAsync(
                async token => await _llm.CompleteAsync(prompt, token),
                cancellationToken);

            _telemetry.RecordSuccess(
                activity, model, response, Stopwatch.GetElapsedTime(started), CostUsd(response));
            return Result<SummaryResult>.Success(
                new SummaryResult(response.Text, response.InputTokens, response.OutputTokens));
        }
        catch (TransientLlmException ex)
        {
            return Fail(activity, model, started, ex, "unavailable",
                ErrorKind.Unavailable, "the model is temporarily unavailable");
        }
        catch (TimeoutRejectedException ex)
        {
            return Fail(activity, model, started, ex, "timeout",
                ErrorKind.Unavailable, "the model did not respond in time");
        }
        catch (LlmNotConfiguredException ex)
        {
            return Fail(activity, model, started, ex, "not_configured",
                ErrorKind.Unavailable, "the model provider is not configured");
        }
        catch (PermanentLlmException ex)
        {
            return Fail(activity, model, started, ex, "provider_error",
                ErrorKind.ProviderError, "the model provider rejected the request");
        }
    }

    private Result<SummaryResult> Fail(
        Activity? activity, string model, long started, Exception ex,
        string errorType, ErrorKind kind, string error)
    {
        _telemetry.RecordFailure(activity, model, Stopwatch.GetElapsedTime(started), errorType);
        _logger.LogWarning(ex, "summarisation failed: {ErrorType}", errorType);
        return Result<SummaryResult>.Failure(kind, error);
    }

    private double? CostUsd(LlmResponse response)
    {
        if (!_options.Pricing.TryGetValue(response.Model, out var price))
        {
            _logger.LogWarning("no price configured for model {Model}; cost not recorded", response.Model);
            return null;
        }

        return (response.InputTokens * price.InputUsdPerMTok
            + response.OutputTokens * price.OutputUsdPerMTok) / 1_000_000;
    }
}
