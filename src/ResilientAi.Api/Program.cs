using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.Retry;
using ResilientAi.Api;
using ResilientAi.Api.Llm;
using ResilientAi.Api.Summarization;
using ResilientAi.Api.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMetrics();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<AiMetrics>();

builder.Services.AddHttpClient<ILlmClient, AnthropicLlmClient>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Resilience around the model call: retry transient failures, then time out.
// See DESIGN.md section 2 for why these values and what they cost.
builder.Services.AddSingleton(_ => new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<TransientLlmException>(),
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromMilliseconds(200),
    })
    .AddTimeout(TimeSpan.FromSeconds(30))
    .Build());

builder.Services.AddScoped<SummarizationService>();

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("ResilientAi"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddSource("ResilientAi"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddMeter(AiMetrics.MeterName));

// Only export when a collector endpoint is configured, so local runs and tests
// do not spew connection errors at a collector that is not there.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    otel.UseOtlpExporter();

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapPost("/summarize", async (
    SummarizeRequest request,
    SummarizationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.SummarizeAsync(request.Text, cancellationToken);
    if (result.IsSuccess)
    {
        var value = result.Value!;
        return Results.Ok(new SummarizeResponse(value.Summary, value.InputTokens, value.OutputTokens));
    }

    return result.ErrorKind == ErrorKind.Validation
        ? Results.BadRequest(new { error = result.Error })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();

public record SummarizeRequest(string Text);

public record SummarizeResponse(string Summary, int InputTokens, int OutputTokens);

/// <summary>Exposed so the test project can drive the app with WebApplicationFactory.</summary>
public partial class Program { }
