using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResilientAi.Api.Llm;
using Xunit;

namespace ResilientAi.Tests;

/// <summary>
/// Drives the real app end to end through WebApplicationFactory, swapping only
/// the model and shrinking the pipeline timings. Pins the transport contract:
/// status-code mapping from the Result, validation, and the health endpoints.
/// </summary>
public class SummarizeEndpointTests
{
    private static WebApplicationFactory<Program> Factory(
        Action<IServiceCollection> replaceModel, IDictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(TestServices.FastTimings);
                if (settings is not null)
                    config.AddInMemoryCollection(settings);
            });
            b.ConfigureTestServices(replaceModel);
        });

    private static WebApplicationFactory<Program> Factory(ILlmClient llm, IDictionary<string, string?>? settings = null) =>
        Factory(services =>
        {
            services.RemoveAll<ILlmClient>();
            services.AddSingleton(llm);
        }, settings);

    // The real AnthropicLlmClient behind a stub handler, so the HttpClient wiring is covered too.
    private static WebApplicationFactory<Program> Factory(StubHandler provider) =>
        Factory(
            services => services.AddHttpClient<ILlmClient, AnthropicLlmClient>()
                .ConfigurePrimaryHttpMessageHandler(() => provider),
            new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "test-key" });

    [Fact]
    public async Task Valid_text_returns_200_with_a_summary()
    {
        using var factory = Factory(new FakeLlmClient());
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "some text to summarise" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SummarizeResponse>();
        Assert.False(string.IsNullOrEmpty(body!.Summary));
    }

    [Fact]
    public async Task Empty_text_returns_400()
    {
        using var factory = Factory(new FakeLlmClient());
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Model_failure_returns_503()
    {
        using var factory = Factory(new FakeLlmClient(alwaysFail: true));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "some text" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Model_timeout_returns_503()
    {
        using var factory = Factory(new FakeLlmClient(alwaysHang: true));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "some text" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Provider_rejection_returns_502_without_the_provider_body()
    {
        using var factory = Factory(new StubHandler(HttpStatusCode.BadRequest, """{"error":"echoes input"}"""));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "some text" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("echoes input", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_api_key_returns_503_with_a_clear_error()
    {
        using var factory = Factory(
            _ => { },
            new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "" });
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/summarize", new { text = "some text" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("not configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Liveness_returns_200_even_without_an_api_key()
    {
        using var factory = Factory(
            new FakeLlmClient(),
            new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "" });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("", HttpStatusCode.ServiceUnavailable)]
    [InlineData("test-key", HttpStatusCode.OK)]
    public async Task Readiness_reflects_whether_the_api_key_is_configured(string key, HttpStatusCode expected)
    {
        using var factory = Factory(
            new FakeLlmClient(),
            new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = key });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(expected, response.StatusCode);
    }
}
