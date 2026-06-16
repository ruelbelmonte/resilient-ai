using System.Diagnostics;
using Polly;
using ResilientAi.Api.Llm;
using ResilientAi.Api.Telemetry;

namespace ResilientAi.Api.Summarization;

public sealed record SummaryResult(string Summary, int InputTokens, int OutputTokens);

/// <summary>
/// Summarises text via the model, wrapped in the resilience pipeline and the
/// Result pattern. Transient failures are retried by the pipeline; if they
/// outlast it, the caller gets a typed Unavailable result, not an exception.
/// Every outcome is recorded to metrics. See DESIGN.md sections 1 to 3.
/// </summary>
public sealed class SummarizationService
{
    private const int MaxInputChars = 20_000;

    // claude-sonnet-4-6 list pricing, USD per token. Keep in sync with the model.
    private const double InputUsdPerToken = 3.0 / 1_000_000;
    private const double OutputUsdPerToken = 15.0 / 1_000_000;

    private readonly ILlmClient _llm;
    private readonly ResiliencePipeline _pipeline;
    private readonly AiMetrics _metrics;
    private readonly ILogger<SummarizationService> _logger;

    public SummarizationService(
        ILlmClient llm,
        ResiliencePipeline pipeline,
        AiMetrics metrics,
        ILogger<SummarizationService> logger)
    {
        _llm = llm;
        _pipeline = pipeline;
        _metrics = metrics;
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
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await _pipeline.ExecuteAsync(
                async token => await _llm.CompleteAsync(prompt, token),
                cancellationToken);

            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var cost = response.InputTokens * InputUsdPerToken
                + response.OutputTokens * OutputUsdPerToken;
            _metrics.Record(elapsedMs, response.InputTokens, response.OutputTokens, cost, "success");

            return Result<SummaryResult>.Success(
                new SummaryResult(response.Text, response.InputTokens, response.OutputTokens));
        }
        catch (Exception ex) when (ex is TransientLlmException or TimeoutException)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _metrics.Record(elapsedMs, 0, 0, 0, "unavailable");
            _logger.LogWarning(ex, "summarisation failed after the resilience pipeline was exhausted");
            return Result<SummaryResult>.Failure(
                ErrorKind.Unavailable, "the model is temporarily unavailable");
        }
    }
}
