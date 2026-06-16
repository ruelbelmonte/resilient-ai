using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Retry;
using ResilientAi.Api;
using ResilientAi.Api.Llm;
using ResilientAi.Api.Summarization;
using ResilientAi.Api.Telemetry;
using Xunit;

namespace ResilientAi.Tests;

public class SummarizationServiceTests
{
    // Same retry policy as production but with zero delay, so the test is fast.
    private static ResiliencePipeline FastPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TransientLlmException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
            })
            .Build();

    private static SummarizationService NewService(ILlmClient llm)
    {
        var meterFactory = new ServiceCollection().AddMetrics()
            .BuildServiceProvider().GetRequiredService<IMeterFactory>();
        return new SummarizationService(
            llm, FastPipeline(), new AiMetrics(meterFactory),
            NullLogger<SummarizationService>.Instance);
    }

    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        var fake = new FakeLlmClient(failBeforeSuccess: 2);
        var result = await NewService(fake).SummarizeAsync("hello world", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, fake.Calls); // 1 initial attempt + 2 retries
    }

    [Fact]
    public async Task Returns_unavailable_when_failures_outlast_the_pipeline()
    {
        var fake = new FakeLlmClient(alwaysFail: true);
        var result = await NewService(fake).SummarizeAsync("hello", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Unavailable, result.ErrorKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_empty_text_as_validation_without_calling_the_model(string text)
    {
        var fake = new FakeLlmClient();
        var result = await NewService(fake).SummarizeAsync(text, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.ErrorKind);
        Assert.Equal(0, fake.Calls);
    }
}
