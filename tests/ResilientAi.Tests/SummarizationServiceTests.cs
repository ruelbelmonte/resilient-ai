using System.Diagnostics;
using ResilientAi.Api;
using ResilientAi.Api.Llm;
using Xunit;

namespace ResilientAi.Tests;

public class SummarizationServiceTests
{
    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        var fake = new FakeLlmClient(failBeforeSuccess: 2);
        var result = await TestServices.Service(fake).SummarizeAsync("hello world", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task Gives_up_after_three_attempts_with_unavailable()
    {
        var fake = new FakeLlmClient(alwaysFail: true);
        var result = await TestServices.Service(fake).SummarizeAsync("hello", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Unavailable, result.ErrorKind);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task Retries_an_attempt_that_times_out()
    {
        var fake = new FakeLlmClient(hangBeforeSuccess: 1);
        var result = await TestServices.Service(fake).SummarizeAsync("hello", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task Returns_unavailable_when_every_attempt_times_out()
    {
        var fake = new FakeLlmClient(alwaysHang: true);
        var result = await TestServices.Service(fake).SummarizeAsync("hello", CancellationToken.None);

        Assert.Equal(ErrorKind.Unavailable, result.ErrorKind);
        Assert.Equal("the model did not respond in time", result.Error);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task Total_timeout_bounds_the_request_across_attempts()
    {
        var fake = new FakeLlmClient(alwaysHang: true);
        var service = TestServices.Service(fake, new Dictionary<string, string?>
        {
            ["Llm:Resilience:AttemptTimeout"] = "00:00:30",
            ["Llm:Resilience:TotalTimeout"] = "00:00:00.200",
        });

        var started = Stopwatch.GetTimestamp();
        var result = await service.SummarizeAsync("hello", CancellationToken.None);

        Assert.Equal(ErrorKind.Unavailable, result.ErrorKind);
        Assert.Equal(1, fake.Calls);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Waits_for_the_providers_retry_after_hint()
    {
        var fake = new FakeLlmClient(
            failBeforeSuccess: 1,
            failWith: () => new TransientLlmException("429", TimeSpan.FromMilliseconds(300)));

        var started = Stopwatch.GetTimestamp();
        var result = await TestServices.Service(fake).SummarizeAsync("hello", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Provider_rejection_is_not_retried()
    {
        var fake = new FakeLlmClient(alwaysFail: true, failWith: () => new PermanentLlmException("model returned 400"));
        var result = await TestServices.Service(fake).SummarizeAsync("hello", CancellationToken.None);

        Assert.Equal(ErrorKind.ProviderError, result.ErrorKind);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Missing_configuration_is_unavailable_and_not_retried()
    {
        var fake = new FakeLlmClient(alwaysFail: true, failWith: () => new LlmNotConfiguredException("no key"));
        var result = await TestServices.Service(fake).SummarizeAsync("hello", CancellationToken.None);

        Assert.Equal(ErrorKind.Unavailable, result.ErrorKind);
        Assert.Equal("the model provider is not configured", result.Error);
        Assert.Equal(1, fake.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_empty_text_as_validation_without_calling_the_model(string text)
    {
        var fake = new FakeLlmClient();
        var result = await TestServices.Service(fake).SummarizeAsync(text, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.ErrorKind);
        Assert.Equal(0, fake.Calls);
    }
}
