using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ResilientAi.Api;
using ResilientAi.Api.Summarization;
using ResilientAi.Api.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMetrics();
builder.Services.AddLlmSummarization(builder.Configuration);

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("ResilientAi"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource(LlmTelemetry.SourceName))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter(LlmTelemetry.SourceName));

// Only export when a collector endpoint is configured, so local runs and tests
// do not spew connection errors at a collector that is not there.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    otel.UseOtlpExporter();

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

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

    var error = new { error = result.Error };
    return result.ErrorKind switch
    {
        ErrorKind.Validation => Results.BadRequest(error),
        ErrorKind.ProviderError => Results.Json(error, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Json(error, statusCode: StatusCodes.Status503ServiceUnavailable),
    };
});

app.Run();

public record SummarizeRequest(string Text);

public record SummarizeResponse(string Summary, int InputTokens, int OutputTokens);

/// <summary>Exposed so the test project can drive the app with WebApplicationFactory.</summary>
public partial class Program { }
